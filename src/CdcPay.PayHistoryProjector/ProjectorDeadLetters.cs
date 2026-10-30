using CdcPay.Contracts;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace CdcPay.PayHistoryProjector;

/// <summary>
/// Publishes business-topic messages the projector cannot use to the dead-letter topic: original
/// key, value, and headers unchanged, plus the <c>dlq.*</c> headers (<see cref="DeadLetterHeaders"/>).
/// </summary>
public sealed class ProjectorDeadLetters(
    IProducer<byte[]?, byte[]> producer,
    IOptions<ProjectorOptions> options,
    ProjectorMetrics metrics,
    TimeProvider clock,
    ILogger<ProjectorDeadLetters> logger)
{
    public const string Component = "pay-history-projector";

    public async Task PublishAsync(ConsumeResult<byte[]?, byte[]> result, string reason, string detail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        Headers headers = [];
        foreach (IHeader header in result.Message.Headers ?? [])
        {
            headers.Add(header.Key, header.GetValueBytes());
        }

        foreach ((string name, byte[] value) in DeadLetterHeaders.Create(
                     reason, detail, Component, result.Topic, result.Partition.Value, result.Offset.Value, clock.GetUtcNow()))
        {
            headers.Add(name, value);
        }

        _ = await producer.ProduceAsync(
            options.Value.DeadLetterTopic,
            new Message<byte[]?, byte[]> { Key = result.Message.Key, Value = result.Message.Value ?? [], Headers = headers },
            cancellationToken);
        metrics.DeadLettered(reason);
        ProjectorLogs.DeadLettered(logger, ProjectorActivities.Position(result.TopicPartitionOffset), reason, detail);
    }
}
