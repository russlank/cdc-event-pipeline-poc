namespace CdcPay.Contracts;

/// <summary>Names and constants of the business event contract (topic, key, versions, event types).</summary>
public static class PayEventContract
{
    /// <summary>Business event topic; the version in the name changes only with a breaking change.</summary>
    public const string Topic = "hr.employee-pay.v1";

    /// <summary>Schema Registry subject (TopicNameStrategy).</summary>
    public const string Subject = Topic + "-value";

    /// <summary>Dead-letter topic for messages that could not be processed.</summary>
    public const string DeadLetterTopic = "hr.dlq";

    /// <summary>Contract version written to <c>schemaVersion</c>; the generated classes are from contracts/avro/v2.</summary>
    public const int SchemaVersion = 2;

    /// <summary>Value of <c>payComponent</c> (contract v2) when the source row has none.</summary>
    public const string DefaultPayComponent = "Base";

    /// <summary>Message key: per-employee ordering.</summary>
    public static string Key(int tenantId, int employeeId) => $"{tenantId}:{employeeId}";
}
