using CdcPay.Contracts;
using CdcPay.Contracts.Events;
using CdcPay.Translation.Assembly;
using CdcPay.Translation.Classification;
using CdcPay.Translation.Raw;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace CdcPay.EventTranslator;

/// <summary>
/// Consumes raw CDC topics and the transaction topic, assembles source transactions,
/// publishes business events, and dead-letters what cannot be translated.
/// Delivery is at-least-once: offsets are committed only after the output of every earlier
/// message is acknowledged. A failure to publish stops the host; on restart it resumes from
/// the committed offsets, and consumers deduplicate by event id.
/// </summary>
public sealed class TranslatorWorker(
    IOptions<KafkaOptions> kafkaOptions,
    IOptions<TranslationOptions> translationOptions,
    IProducer<string, PayEvent> eventProducer,
    DeadLetterPublisher deadLetters,
    TranslatorStatus status,
    TimeProvider clock,
    ILogger<TranslatorWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMilliseconds(500);

    private readonly KafkaOptions _kafka = kafkaOptions.Value;
    private readonly EventClassifier _classifier = new(translationOptions.Value);
    private TransactionAssembler _assembler = new(clock, translationOptions.Value.TransactionTimeout);
    private OffsetTracker _offsets = new();
    private bool _resetRequested;
    private ErrorCode _lastErrorCode = ErrorCode.NoError;
    private DateTimeOffset _lastErrorLoggedAt = DateTimeOffset.MinValue;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => ConsumeAsync(stoppingToken), stoppingToken);

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        using IConsumer<byte[]?, byte[]> consumer = BuildConsumer();
        consumer.Subscribe([_kafka.RawTopicPattern, _kafka.TransactionTopic]);
        TranslatorLogs.Consuming(logger, _kafka.RawTopicPattern, _kafka.TransactionTopic, _kafka.ConsumerGroup);

        DateTimeOffset nextCommit = clock.GetUtcNow() + _kafka.CommitInterval;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<byte[]?, byte[]>? result = Poll(consumer);
                if (_resetRequested)
                {
                    _assembler = new TransactionAssembler(clock, translationOptions.Value.TransactionTimeout);
                    _offsets = new OffsetTracker();
                    _resetRequested = false;
                }

                if (result is { IsPartitionEOF: false, Message: not null })
                {
                    await AcceptAsync(result, stoppingToken);
                }

                _assembler.ExpireStale();
                foreach (AssemblerOutput output in _assembler.Drain())
                {
                    await HandleAsync(output, stoppingToken);
                    foreach (KafkaPosition position in output.Positions)
                    {
                        _offsets.Complete(position);
                    }
                }

                if (clock.GetUtcNow() >= nextCommit)
                {
                    Commit(consumer);
                    nextCommit = clock.GetUtcNow() + _kafka.CommitInterval;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: fall through to the final commit.
        }

        Commit(consumer);
        consumer.Close();
    }

    /// <summary>
    /// Non-fatal consumer errors are transient (for example the raw topics do not exist yet
    /// because Debezium has not snapshotted): log them, at most every 30 s per error code,
    /// and keep polling. Fatal errors stop the host.
    /// </summary>
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
                TranslatorLogs.ConsumeError(logger, exception.Error.Code.ToString(), exception.Error.Reason);
                _lastErrorCode = exception.Error.Code;
                _lastErrorLoggedAt = now;
            }

            return null;
        }
    }

    private async Task AcceptAsync(ConsumeResult<byte[]?, byte[]> result, CancellationToken cancellationToken)
    {
        KafkaPosition position = new(result.Topic, result.Partition.Value, result.Offset.Value);
        RawMessage message = new(position, result.Message.Key, result.Message.Value ?? []);
        _offsets.Register(position);

        try
        {
            if (result.Topic == _kafka.TransactionTopic)
            {
                _assembler.Accept(DebeziumParser.ParseTransactionMarker(message));
            }
            else
            {
                _assembler.Accept(DebeziumParser.ParseChange(message));
            }
        }
        catch (DebeziumFormatException exception)
        {
            await deadLetters.PublishAsync(message, DeadLetterReasons.Unparseable, exception.Message, cancellationToken);
            _offsets.Complete(position);
        }
    }

    private async Task HandleAsync(AssemblerOutput output, CancellationToken cancellationToken)
    {
        if (output is AbandonedTransaction gaveUp)
        {
            // Logged even without rows, so an abandoned transaction is never silent.
            TranslatorLogs.TransactionAbandoned(logger, gaveUp.TransactionId, gaveUp.Rows.Count, gaveUp.Detail);
        }

        IReadOnlyList<TranslationOutcome> outcomes = output switch
        {
            CompletedTransaction transaction => _classifier.Classify(transaction),
            SnapshotRow snapshot => _classifier.Classify(snapshot),
            AbandonedTransaction abandoned when abandoned.Rows.Count > 0 =>
                [new DeadLetterOutcome(DeadLetterReasons.IncompleteTransaction, $"transaction {abandoned.TransactionId}: {abandoned.Detail}", abandoned.Rows)],
            _ => [],
        };

        foreach (TranslationOutcome outcome in outcomes)
        {
            switch (outcome)
            {
                case EventOutcome published:
                    _ = await eventProducer.ProduceAsync(
                        PayEventContract.Topic,
                        new Message<string, PayEvent> { Key = published.Key, Value = published.Event },
                        cancellationToken);
                    TranslatorLogs.EventPublished(logger, published.Event.eventType, published.Event.eventId);
                    break;
                case IgnoredOutcome ignored:
                    TranslatorLogs.RowsIgnored(logger, ignored.Rows.Count, ignored.Reason);
                    break;
                case DeadLetterOutcome failed:
                    foreach (RawChange row in failed.Rows)
                    {
                        await deadLetters.PublishAsync(row.Message, failed.Reason, failed.Detail, cancellationToken);
                    }

                    break;
            }
        }
    }

    private void Commit(IConsumer<byte[]?, byte[]> consumer)
    {
        HashSet<(string, int)> assigned = [.. consumer.Assignment.Select(partition => (partition.Topic, partition.Partition.Value))];
        List<TopicPartitionOffset> offsets =
        [
            .. _offsets.CommitPoints()
                .Where(point => assigned.Contains(point.Key))
                .Select(point => new TopicPartitionOffset(point.Key.Topic, new Partition(point.Key.Partition), new Offset(point.Value))),
        ];
        if (offsets.Count == 0)
        {
            return;
        }

        consumer.Commit(offsets);
        TranslatorLogs.OffsetsCommitted(logger, offsets.Count);
    }

    private IConsumer<byte[]?, byte[]> BuildConsumer()
    {
        ConsumerConfig config = new()
        {
            BootstrapServers = _kafka.BootstrapServers,
            GroupId = _kafka.ConsumerGroup,
            ClientId = $"{_kafka.ConsumerGroup}-{Environment.MachineName}",
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            // New cdc.* topics (for example the signal table's) are picked up within seconds.
            TopicMetadataRefreshIntervalMs = 10_000,
        };

        return new ConsumerBuilder<byte[]?, byte[]>(config)
            .SetPartitionsAssignedHandler((_, partitions) =>
            {
                status.AssignedPartitions = partitions.Count;
                TranslatorLogs.PartitionsAssigned(logger, partitions.Count);
            })
            .SetPartitionsRevokedHandler((_, partitions) =>
            {
                status.AssignedPartitions = 0;
                _resetRequested = true;
                TranslatorLogs.PartitionsRevoked(logger, partitions.Count);
            })
            .Build();
    }
}
