using System.Diagnostics.Metrics;

namespace CdcPay.EventTranslator;

/// <summary>
/// Translator instruments (meter <see cref="MeterName"/>). Tags are bounded: <c>event_type</c>
/// from the five event types, <c>reason</c> from the dead-letter and ignore reason codes.
/// </summary>
public sealed class TranslatorMetrics
{
    public const string MeterName = "CdcPay.EventTranslator";

    private readonly Counter<long> _produced;
    private readonly Counter<long> _deadLettered;
    private readonly Counter<long> _ignored;
    private readonly Counter<long> _abandoned;
    private readonly Histogram<double> _endToEndLag;

    public TranslatorMetrics(IMeterFactory meterFactory, TranslatorStatus status)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(status);

        Meter meter = meterFactory.Create(MeterName);
        _produced = meter.CreateCounter<long>("cdcpay.translator.events.produced", "{event}",
            "Business events published, by event type.");
        _deadLettered = meter.CreateCounter<long>("cdcpay.translator.dlq.produced", "{message}",
            "Raw messages sent to the dead-letter topic, by reason.");
        _ignored = meter.CreateCounter<long>("cdcpay.translator.changes.ignored", "{change}",
            "Source rows without pay meaning, by reason.");
        // Usually 1: Debezium emits a transaction's END only when the next transaction begins.
        meter.CreateObservableGauge("cdcpay.translator.transactions.open", () => status.OpenTransactions, "{transaction}",
            "Source transactions seen but not complete yet.");
        _abandoned = meter.CreateCounter<long>("cdcpay.translator.transactions.abandoned", "{transaction}",
            "Source transactions given up as incomplete after the timeout.");
        _endToEndLag = meter.CreateHistogram("cdcpay.translator.end_to_end.lag", "s",
            "Time from the source commit (recordedAt) to the publication of the event.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 300, 3600] });
    }

    public void Produced(string eventType, TimeSpan lag)
    {
        KeyValuePair<string, object?> tag = new("event_type", eventType);
        _produced.Add(1, tag);
        _endToEndLag.Record(Math.Max(0, lag.TotalSeconds), tag);
    }

    public void DeadLettered(string reason) => _deadLettered.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void Abandoned() => _abandoned.Add(1);

    public void Ignored(string reason, int rows) => _ignored.Add(rows, new KeyValuePair<string, object?>("reason", reason));
}
