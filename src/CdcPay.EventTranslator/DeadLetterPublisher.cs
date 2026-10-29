using System.Globalization;
using System.Text;
using CdcPay.Contracts;
using CdcPay.Translation.Raw;
using Confluent.Kafka;

namespace CdcPay.EventTranslator;

/// <summary>
/// Publishes failed messages to the dead-letter topic: original key and value bytes unchanged,
/// error details in <c>dlq.*</c> headers.
/// </summary>
public sealed class DeadLetterPublisher(IProducer<byte[]?, byte[]> producer, TimeProvider clock, ILogger<DeadLetterPublisher> logger)
{
    public const string Component = "event-translator";

    public async Task PublishAsync(RawMessage message, string reason, string detail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        Headers headers = new()
        {
            { "dlq.reason", Utf8(reason) },
            { "dlq.error", Utf8(detail) },
            { "dlq.component", Utf8(Component) },
            { "dlq.source.topic", Utf8(message.Position.Topic) },
            { "dlq.source.partition", Utf8(message.Position.Partition.ToString(CultureInfo.InvariantCulture)) },
            { "dlq.source.offset", Utf8(message.Position.Offset.ToString(CultureInfo.InvariantCulture)) },
            { "dlq.timestamp", Utf8(clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)) },
        };

        _ = await producer.ProduceAsync(
            PayEventContract.DeadLetterTopic,
            new Message<byte[]?, byte[]> { Key = message.Key, Value = message.Value, Headers = headers },
            cancellationToken);
        TranslatorLogs.DeadLettered(logger, message.Position.ToString(), reason, detail);
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
