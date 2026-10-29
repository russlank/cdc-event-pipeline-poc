using CdcPay.PayHistory.Storage;

namespace CdcPay.PayHistoryProjector;

/// <summary>
/// Log messages of the projector. Event ids 3000-3099. Values are bounded: event types, event
/// ids, Kafka positions, and counts; never row content (<c>CdcPay.Contracts.DiagnosticValues</c>).
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

    [LoggerMessage(EventId = 3005, Level = LogLevel.Warning, Message = "{Operation} failed with {ErrorType}, a transient error; retrying in {RetryDelay}")]
    public static partial void Retrying(ILogger logger, string operation, Type errorType, TimeSpan retryDelay);

    [LoggerMessage(EventId = 3010, Level = LogLevel.Information, Message = "{Result} {EventType} {EventId} from {Position}")]
    public static partial void EventProjected(ILogger logger, ProjectionResult result, string eventType, Guid eventId, string position);

    [LoggerMessage(EventId = 3012, Level = LogLevel.Warning, Message = "Dead-lettered {Position}: {Reason} ({Detail})")]
    public static partial void DeadLettered(ILogger logger, string position, string reason, string detail);

    [LoggerMessage(EventId = 3013, Level = LogLevel.Critical, Message = "Projecting {Position} failed with {ErrorType}; stopping")]
    public static partial void ProjectionFailed(ILogger logger, string position, Type errorType);
}
