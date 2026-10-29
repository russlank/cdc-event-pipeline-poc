namespace CdcPay.Translation.Raw;

/// <summary>Where a Kafka message was read from.</summary>
public readonly record struct KafkaPosition(string Topic, int Partition, long Offset)
{
    public override string ToString() => $"{Topic}[{Partition}]@{Offset}";
}

/// <summary>A consumed message as received, kept so it can be dead-lettered unchanged.</summary>
public sealed record RawMessage(KafkaPosition Position, byte[]? Key, byte[] Value);
