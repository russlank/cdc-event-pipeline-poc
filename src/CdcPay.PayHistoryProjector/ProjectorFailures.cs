using System.Net;
using Confluent.SchemaRegistry;
using Npgsql;

namespace CdcPay.PayHistoryProjector;

/// <summary>
/// Which failures are worth waiting for. A transient failure is retried on the same message and
/// never dead-letters it; any other failure while reading a message means the message itself is
/// bad (dead letter), and any other failure while writing stops the host.
/// </summary>
public static class ProjectorFailures
{
    /// <summary>Dead-letter reason: the value is not an Avro business event of a registered contract version.</summary>
    public const string Undeserializable = "Undeserializable";

    /// <summary>Dead-letter reason: a business event the read model cannot apply (payload does not match the type).</summary>
    public const string InvalidEvent = "InvalidEvent";

    /// <summary>
    /// The read model or the Schema Registry is unreachable or overloaded: connection errors,
    /// failover, serialization conflicts, registry 5xx, 408, and 429.
    /// </summary>
    public static bool IsTransient(Exception exception) => exception switch
    {
        NpgsqlException npgsql => npgsql.IsTransient,
        // Before HttpRequestException, which it derives from: a 404 (unknown schema id) is not transient.
        SchemaRegistryException registry => (int)registry.Status >= 500
            || registry.Status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests,
        HttpRequestException => true,
        _ => false,
    };
}
