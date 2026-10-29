using System.Diagnostics.Metrics;
using CdcPay.PayHistory.Storage;

namespace CdcPay.PayHistoryProjector;

/// <summary>
/// Projector counters (meter <see cref="MeterName"/>). Tags are bounded: result and reason codes.
/// </summary>
public sealed class ProjectorMetrics
{
    public const string MeterName = "CdcPay.PayHistoryProjector";

    private readonly Counter<long> _applied;
    private readonly Counter<long> _duplicate;
    private readonly Counter<long> _deadLettered;

    public ProjectorMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        Meter meter = meterFactory.Create(MeterName);
        _applied = meter.CreateCounter<long>("cdcpay.projector.events.applied", "{event}",
            "Events applied to the read model; result=changed when rows changed, unchanged when it already said the same.");
        _duplicate = meter.CreateCounter<long>("cdcpay.projector.events.duplicate", "{event}",
            "Events skipped because their id was processed before.");
        _deadLettered = meter.CreateCounter<long>("cdcpay.projector.dlq.produced", "{message}",
            "Messages sent to the dead-letter topic, by reason.");
    }

    public void Projected(ProjectionResult result)
    {
        switch (result)
        {
            case ProjectionResult.Duplicate:
                _duplicate.Add(1);
                break;
            case ProjectionResult.Applied:
                _applied.Add(1, new KeyValuePair<string, object?>("result", "changed"));
                break;
            case ProjectionResult.NoChange:
                _applied.Add(1, new KeyValuePair<string, object?>("result", "unchanged"));
                break;
        }
    }

    public void DeadLettered(string reason) => _deadLettered.Add(1, new KeyValuePair<string, object?>("reason", reason));
}
