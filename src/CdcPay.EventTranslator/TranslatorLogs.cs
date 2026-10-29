namespace CdcPay.EventTranslator;

/// <summary>
/// Log messages of the translator. Event ids 2000-2099. Values are bounded: event types, reason
/// codes, Kafka positions, and counts; never names, amounts, or other row values.
/// </summary>
internal static partial class TranslatorLogs
{
    [LoggerMessage(EventId = 2000, Level = LogLevel.Information, Message = "Consuming {RawTopicPattern} and {TransactionTopic} as group {ConsumerGroup}")]
    public static partial void Consuming(ILogger logger, string rawTopicPattern, string transactionTopic, string consumerGroup);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Partitions assigned: {PartitionCount}")]
    public static partial void PartitionsAssigned(ILogger logger, int partitionCount);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Partitions revoked: {PartitionCount}; open transactions are dropped and re-read from the committed offsets")]
    public static partial void PartitionsRevoked(ILogger logger, int partitionCount);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Warning, Message = "Kafka consumer error {ErrorCode}: {ErrorReason}; retrying")]
    public static partial void ConsumeError(ILogger logger, string errorCode, string errorReason);

    [LoggerMessage(EventId = 2010, Level = LogLevel.Information, Message = "Published {EventType} {EventId}")]
    public static partial void EventPublished(ILogger logger, string eventType, Guid eventId);

    [LoggerMessage(EventId = 2011, Level = LogLevel.Debug, Message = "Ignored {RowCount} rows: {Reason}")]
    public static partial void RowsIgnored(ILogger logger, int rowCount, string reason);

    [LoggerMessage(EventId = 2012, Level = LogLevel.Warning, Message = "Dead-lettered {Position}: {Reason} ({Detail})")]
    public static partial void DeadLettered(ILogger logger, string position, string reason, string detail);

    [LoggerMessage(EventId = 2020, Level = LogLevel.Debug, Message = "Committed offsets for {PartitionCount} partitions")]
    public static partial void OffsetsCommitted(ILogger logger, int partitionCount);
}
