using System.Diagnostics;
using System.Text;
using CdcPay.Contracts;
using Confluent.Kafka;

namespace CdcPay.PayHistoryProjector;

/// <summary>
/// Spans of the projector (activity source <see cref="SourceName"/>). Attributes are bounded:
/// topic, Kafka position, event type, event id, and outcome.
/// </summary>
public static class ProjectorActivities
{
    public const string SourceName = ProjectorMetrics.MeterName;

    private static readonly ActivitySource Source = new(SourceName);

    /// <summary>
    /// A consumer span for one message, continuing the trace of the translator's span when the
    /// message carries a <c>traceparent</c> header; null when nobody listens.
    /// </summary>
    public static Activity? StartProcess(string topic, string position, Headers? headers)
    {
        string? parent = headers is not null && headers.TryGetLastBytes(PayEventContract.TraceParentHeader, out byte[] value)
            ? Encoding.ASCII.GetString(value)
            : null;
        Activity? activity = ActivityContext.TryParse(parent, null, out ActivityContext context)
            ? Source.StartActivity($"{topic} process", ActivityKind.Consumer, context)
            : Source.StartActivity($"{topic} process", ActivityKind.Consumer);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", topic);
        activity?.SetTag("messaging.operation.type", "process");
        activity?.SetTag("cdcpay.position", position);
        return activity;
    }

    public static void SetEvent(Activity? activity, string eventType, Guid eventId)
    {
        activity?.SetTag("cdcpay.event_type", eventType);
        activity?.SetTag("cdcpay.event_id", eventId.ToString());
    }

    /// <summary>The outcome: a <c>ProjectionResult</c> name or a dead-letter reason.</summary>
    public static void SetOutcome(Activity? activity, string outcome) => activity?.SetTag("cdcpay.outcome", outcome);
}
