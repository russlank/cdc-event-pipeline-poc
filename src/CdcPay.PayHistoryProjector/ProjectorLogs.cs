using CdcPay.PayHistory.Storage;

namespace CdcPay.PayHistoryProjector;

/// <summary>
/// Log messages of the projector. Event ids 3000-3099. Values are bounded: event types, event
/// ids, Kafka positions, and counts; never amounts or other pay values.
/// </summary>
internal static partial class ProjectorLogs
{
    [LoggerMessage(EventId = 3000, Level = LogLevel.Information, Message = "Consuming {Topic} as group {ConsumerGroup}")]
    public static partial void Consuming(ILogger logger, string topic, string consumerGroup);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Partitions assigned: {PartitionCount}")]
    public static partial void PartitionsAssigned(ILogger logger, int partitionCount);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information, Message = "Partitions revoked: {PartitionCount}")]
    public static partial void PartitionsRevoked(ILogger logger, int partitionCount);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Warning, Message = "Kafka consumer error {ErrorCode}: {ErrorReason}; retrying")]
    public static partial void ConsumeError(ILogger logger, string errorCode, string errorReason);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Information, Message = "Read-model schema applied ({ScriptCount} scripts)")]
    public static partial void SchemaApplied(ILogger logger, int scriptCount);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Warning, Message = "Read model unavailable ({Operation}); retrying in {RetryDelay}")]
    public static partial void ReadModelUnavailable(ILogger logger, Exception exception, string operation, TimeSpan retryDelay);

    [LoggerMessage(EventId = 3006, Level = LogLevel.Warning, Message = "Schema Registry unavailable; retrying in {RetryDelay}")]
    public static partial void SchemaRegistryUnavailable(ILogger logger, Exception exception, TimeSpan retryDelay);

    [LoggerMessage(EventId = 3010, Level = LogLevel.Information, Message = "{Result} {EventType} {EventId} from {Position}")]
    public static partial void EventProjected(ILogger logger, ProjectionResult result, string eventType, Guid eventId, string position);

    [LoggerMessage(EventId = 3011, Level = LogLevel.Critical, Message = "Cannot project the message at {Position}: {Detail}; stopping")]
    public static partial void UnprojectableMessage(ILogger logger, Exception exception, string position, string detail);
}
