using System.ComponentModel.DataAnnotations;
using CdcPay.Contracts;

namespace CdcPay.PayHistoryProjector;

/// <summary>Kafka and Schema Registry settings (configuration section <c>Kafka</c>).</summary>
public sealed class ProjectorOptions
{
    public const string SectionName = "Kafka";

    [Required]
    public string BootstrapServers { get; set; } = string.Empty;

    [Required]
    [Url]
    public string SchemaRegistryUrl { get; set; } = string.Empty;

    [Required]
    public string ConsumerGroup { get; set; } = "cdcpay-pay-history-projector";

    [Required]
    public string Topic { get; set; } = PayEventContract.Topic;

    /// <summary>Where messages that are not usable business events go.</summary>
    [Required]
    public string DeadLetterTopic { get; set; } = PayEventContract.DeadLetterTopic;

    /// <summary>How often offsets of applied events are committed.</summary>
    public TimeSpan CommitInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Wait between attempts when the read model is unavailable.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);
}
