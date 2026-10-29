using System.Diagnostics;
using CdcPay.Contracts.Events;
using CdcPay.PayHistory;
using CdcPay.PayHistory.Storage;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CdcPay.PayHistoryProjector;

/// <summary>
/// Projects business events into the bitemporal read model, one database transaction per event.
/// An event's offset is stored only after its transaction commits (or after the message is
/// dead-lettered) and is committed periodically, so delivery is at-least-once;
/// <c>processed_event</c> turns a redelivery into a no-op.
/// Failures (<see cref="ProjectorFailures"/>): a transient one is retried on the same message and
/// never dead-letters it; a message that is not a usable business event goes to the dead-letter
/// topic and the projector moves on; any other failure stops the host, which resumes from the last
/// committed offset.
/// </summary>
public sealed class ProjectorWorker(
    IOptions<ProjectorOptions> options,
    NpgsqlDataSource dataSource,
    PayHistoryStore store,
    ISchemaRegistryClient schemaRegistry,
    ProjectorDeadLetters deadLetters,
    ProjectorMetrics metrics,
    ProjectorStatus status,
    TimeProvider clock,
    ILogger<ProjectorWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMilliseconds(500);

    private readonly ProjectorOptions _options = options.Value;
    private readonly AvroDeserializer<PayEvent> _deserializer = new(schemaRegistry);
    private ErrorCode _lastErrorCode = ErrorCode.NoError;
    private DateTimeOffset _lastErrorLoggedAt = DateTimeOffset.MinValue;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => RunAsync(stoppingToken), stoppingToken);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        int scripts = await RetryTransientAsync("apply schema", async () =>
        {
            await ReadModelSchema.ApplyAsync(dataSource, stoppingToken);
            return ReadModelSchema.ScriptNames.Count;
        }, stoppingToken);
        status.SchemaApplied = true;
        ProjectorLogs.SchemaApplied(logger, scripts);

        using IConsumer<byte[]?, byte[]> consumer = BuildConsumer();
        consumer.Subscribe(_options.Topic);
        ProjectorLogs.Consuming(logger, _options.Topic, _options.ConsumerGroup);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (Poll(consumer) is { IsPartitionEOF: false, Message: not null } result)
                {
                    await HandleAsync(result, stoppingToken);
                    consumer.StoreOffset(result);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: Close commits the stored offsets.
        }

        consumer.Close();
    }

    private async Task HandleAsync(ConsumeResult<byte[]?, byte[]> result, CancellationToken stoppingToken)
    {
        string position = result.TopicPartitionOffset.ToString();
        using Activity? activity = ProjectorActivities.StartProcess(result.Topic, position, result.Message.Headers);
        PayEvent? payEvent;
        try
        {
            SerializationContext context = new(MessageComponentType.Value, result.Topic, result.Message.Headers);
            payEvent = await RetryTransientAsync(
                "read schema", () => _deserializer.DeserializeAsync(result.Message.Value, result.Message.Value is null, context), stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The exception text may quote the message; only its type is recorded.
            ProjectorActivities.SetOutcome(activity, ProjectorFailures.Undeserializable);
            await deadLetters.PublishAsync(
                result, ProjectorFailures.Undeserializable, $"{exception.GetType().Name}: not a business event of the contract", stoppingToken);
            return;
        }

        if (payEvent is null)
        {
            ProjectorActivities.SetOutcome(activity, ProjectorFailures.Undeserializable);
            await deadLetters.PublishAsync(result, ProjectorFailures.Undeserializable, "empty value", stoppingToken);
            return;
        }

        ProjectorActivities.SetEvent(activity, payEvent.eventType, payEvent.eventId);
        try
        {
            ProjectionResult projected = await RetryTransientAsync("project", () => store.ProjectAsync(payEvent, stoppingToken), stoppingToken);
            metrics.Projected(projected);
            ProjectorActivities.SetOutcome(activity, projected.ToString());
            ProjectorLogs.EventProjected(logger, projected, payEvent.eventType, payEvent.eventId, position);
        }
        catch (PayEventException exception)
        {
            ProjectorActivities.SetOutcome(activity, ProjectorFailures.InvalidEvent);
            await deadLetters.PublishAsync(result, ProjectorFailures.InvalidEvent, exception.Message, stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Only the type: database errors can quote row values in their detail.
            Type errorType = exception.GetType();
            ProjectorLogs.ProjectionFailed(logger, position, errorType);
            activity?.SetStatus(ActivityStatusCode.Error, errorType.Name);
            throw;
        }
    }

    /// <summary>Retries transient failures (<see cref="ProjectorFailures.IsTransient"/>) until stopped.</summary>
    private async Task<T> RetryTransientAsync<T>(string operation, Func<Task<T>> action, CancellationToken stoppingToken)
    {
        while (true)
        {
            try
            {
                return await action();
            }
            catch (Exception exception) when (ProjectorFailures.IsTransient(exception) && !stoppingToken.IsCancellationRequested)
            {
                Type errorType = exception.GetType();
                ProjectorLogs.Retrying(logger, operation, errorType, _options.RetryDelay);
                await Task.Delay(_options.RetryDelay, clock, stoppingToken);
            }
        }
    }

    /// <summary>Non-fatal consumer errors are logged at most every 30 s per error code; fatal ones stop the host.</summary>
    private ConsumeResult<byte[]?, byte[]>? Poll(IConsumer<byte[]?, byte[]> consumer)
    {
        try
        {
            return consumer.Consume(PollTimeout);
        }
        catch (ConsumeException exception) when (!exception.Error.IsFatal)
        {
            DateTimeOffset now = clock.GetUtcNow();
            if (exception.Error.Code != _lastErrorCode || now - _lastErrorLoggedAt >= TimeSpan.FromSeconds(30))
            {
                ProjectorLogs.ConsumeError(logger, exception.Error.Code.ToString(), exception.Error.Reason);
                _lastErrorCode = exception.Error.Code;
                _lastErrorLoggedAt = now;
            }

            return null;
        }
    }

    private IConsumer<byte[]?, byte[]> BuildConsumer()
    {
        ConsumerConfig config = new()
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.ConsumerGroup,
            ClientId = $"{_options.ConsumerGroup}-{Environment.MachineName}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            // Offsets are stored by hand after the database commit; the client commits what is stored.
            EnableAutoOffsetStore = false,
            EnableAutoCommit = true,
            AutoCommitIntervalMs = (int)_options.CommitInterval.TotalMilliseconds,
        };

        return new ConsumerBuilder<byte[]?, byte[]>(config)
            .SetPartitionsAssignedHandler((_, partitions) =>
            {
                status.AssignedPartitions = partitions.Count;
                ProjectorLogs.PartitionsAssigned(logger, partitions.Count);
            })
            .SetPartitionsRevokedHandler((_, partitions) =>
            {
                status.AssignedPartitions = 0;
                ProjectorLogs.PartitionsRevoked(logger, partitions.Count);
            })
            .Build();
    }
}
