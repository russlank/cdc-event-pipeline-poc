using CdcPay.Contracts;
using CdcPay.Translation.Raw;
using Confluent.Kafka;

namespace CdcPay.EventTranslator;

/// <summary>
/// Publishes failed messages to the dead-letter topic: original key and value bytes unchanged,
/// error details in <c>dlq.*</c> headers (<see cref="DeadLetterHeaders"/>).
/// </summary>
public sealed class DeadLetterPublisher(IProducer<byte[]?, byte[]> producer, TimeProvider clock, ILogger<DeadLetterPublisher> logger)
{
    public const string Component = "event-translator";

    public async Task PublishAsync(RawMessage message, string reason, string detail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        Headers headers = [];
        foreach ((string name, byte[] value) in DeadLetterHeaders.Create(
                     reason, detail, Component, message.Position.Topic, message.Position.Partition, message.Position.Offset, clock.GetUtcNow()))
        {
            headers.Add(name, value);
        }

        _ = await producer.ProduceAsync(
            PayEventContract.DeadLetterTopic,
            new Message<byte[]?, byte[]> { Key = message.Key, Value = message.Value, Headers = headers },
            cancellationToken);
        TranslatorLogs.DeadLettered(logger, message.Position.ToString(), reason, detail);
    }
}
