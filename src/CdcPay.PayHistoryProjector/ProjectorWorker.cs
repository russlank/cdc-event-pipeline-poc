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
/// An event's offset is stored only after its transaction commits and is committed periodically,
/// so delivery is at-least-once; <c>processed_event</c> turns a redelivery into a no-op.
/// Transient database errors are retried without moving on; a message that cannot be projected
/// stops the host, and it resumes from the last committed offset.
/// </summary>
public sealed class ProjectorWorker(
    IOptions<ProjectorOptions> options,
    NpgsqlDataSource dataSource,
    PayHistoryStore store,
    ISchemaRegistryClient schemaRegistry,
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
        int scripts = await WithReadModelAsync("apply schema", async () =>
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
                    await ProjectAsync(result, stoppingToken);
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

    private async Task ProjectAsync(ConsumeResult<byte[]?, byte[]> result, CancellationToken stoppingToken)
    {
        string position = result.TopicPartitionOffset.ToString();
        PayEvent payEvent = await DeserializeAsync(result, position, stoppingToken);
        try
        {
            ProjectionResult projected = await WithReadModelAsync("project", () => store.ProjectAsync(payEvent, stoppingToken), stoppingToken);
            ProjectorLogs.EventProjected(logger, projected, payEvent.eventType, payEvent.eventId, position);
        }
        catch (PayEventException exception)
        {
            ProjectorLogs.UnprojectableMessage(logger, exception, position, exception.Message);
            throw;
        }
    }

    /// <summary>
    /// An unreachable Schema Registry is retried; any other failure means the message is not a
    /// business event of the contract, and the host stops.
    /// </summary>
    private async Task<PayEvent> DeserializeAsync(ConsumeResult<byte[]?, byte[]> result, string position, CancellationToken stoppingToken)
    {
        SerializationContext context = new(MessageComponentType.Value, result.Topic, result.Message.Headers);
        while (true)
        {
            try
            {
                return await _deserializer.DeserializeAsync(result.Message.Value, result.Message.Value is null, context);
            }
            catch (HttpRequestException exception) when (!stoppingToken.IsCancellationRequested)
            {
                ProjectorLogs.SchemaRegistryUnavailable(logger, exception, _options.RetryDelay);
                await Task.Delay(_options.RetryDelay, clock, stoppingToken);
            }
            catch (Exception exception) when (exception is not HttpRequestException and not OperationCanceledException)
            {
                ProjectorLogs.UnprojectableMessage(logger, exception, position, "not a business event of the contract");
                throw;
            }
        }
    }

    /// <summary>Retries transient database errors (connection refused, failover, serialization) until stopped.</summary>
    private async Task<T> WithReadModelAsync<T>(string operation, Func<Task<T>> action, CancellationToken stoppingToken)
    {
        while (true)
        {
            try
            {
                return await action();
            }
            catch (NpgsqlException exception) when (exception.IsTransient && !stoppingToken.IsCancellationRequested)
            {
                ProjectorLogs.ReadModelUnavailable(logger, exception, operation, _options.RetryDelay);
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
