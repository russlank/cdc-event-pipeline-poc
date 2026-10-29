using System.Diagnostics;
using System.Text;
using CdcPay.Contracts;
using Confluent.Kafka;

namespace CdcPay.EventTranslator;

/// <summary>
/// Spans of the translator (activity source <see cref="SourceName"/>). Attributes are bounded:
/// topic, event type, and event id.
/// </summary>
public static class TranslatorActivities
{
    public const string SourceName = TranslatorMetrics.MeterName;

    private static readonly ActivitySource Source = new(SourceName);

    /// <summary>A producer span for one business event; null when nobody listens.</summary>
    public static Activity? StartPublish(string eventType, Guid eventId)
    {
        Activity? activity = Source.StartActivity($"{PayEventContract.Topic} publish", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", PayEventContract.Topic);
        activity?.SetTag("messaging.operation.type", "send");
        activity?.SetTag("cdcpay.event_type", eventType);
        activity?.SetTag("cdcpay.event_id", eventId.ToString());
        return activity;
    }

    /// <summary>The W3C <c>traceparent</c> header that lets the projector continue the trace; null without a span.</summary>
    public static Headers? TraceHeaders(Activity? activity) =>
        activity is { IdFormat: ActivityIdFormat.W3C, Id: { } id }
            ? new Headers { { PayEventContract.TraceParentHeader, Encoding.ASCII.GetBytes(id) } }
            : null;
}
