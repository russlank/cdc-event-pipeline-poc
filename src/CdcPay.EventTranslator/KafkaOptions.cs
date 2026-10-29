using System.ComponentModel.DataAnnotations;

namespace CdcPay.EventTranslator;

/// <summary>Kafka and Schema Registry settings (configuration section <c>Kafka</c>).</summary>
public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    [Required]
    public string BootstrapServers { get; set; } = string.Empty;

    [Required]
    [Url]
    public string SchemaRegistryUrl { get; set; } = string.Empty;

    [Required]
    public string ConsumerGroup { get; set; } = "cdcpay-event-translator";

    /// <summary>Raw CDC topics, as a librdkafka regex subscription (leading <c>^</c>).</summary>
    [Required]
    public string RawTopicPattern { get; set; } = @"^cdc\.HrCore\.dbo\..*";

    [Required]
    public string TransactionTopic { get; set; } = "cdc.transaction";

    /// <summary>How often safe offsets are committed.</summary>
    public TimeSpan CommitInterval { get; set; } = TimeSpan.FromSeconds(1);
}
