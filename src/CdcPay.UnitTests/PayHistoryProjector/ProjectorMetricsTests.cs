extern alias projector;

using System.Diagnostics.Metrics;
using CdcPay.PayHistory.Storage;
using Microsoft.Extensions.DependencyInjection;
using projector::CdcPay.PayHistoryProjector;

namespace CdcPay.UnitTests.PayHistoryProjector;

public sealed class ProjectorMetricsTests
{
    [Fact]
    public void Projected_CountsAppliedDuplicateAndDeadLettered()
    {
        using ServiceProvider services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        ProjectorMetrics metrics = new(services.GetRequiredService<IMeterFactory>());
        List<(string Instrument, long Value, string? Tag)> measurements = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ProjectorMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, tags.Length == 0 ? null : $"{tags[0].Key}={tags[0].Value}")));
        listener.Start();

        metrics.Projected(ProjectionResult.Applied);
        metrics.Projected(ProjectionResult.NoChange);
        metrics.Projected(ProjectionResult.Duplicate);
        metrics.DeadLettered(ProjectorFailures.Undeserializable);

        Assert.Equal(
            [
                ("cdcpay.projector.events.applied", 1L, (string?)"result=changed"),
                ("cdcpay.projector.events.applied", 1L, "result=unchanged"),
                ("cdcpay.projector.events.duplicate", 1L, null),
                ("cdcpay.projector.dlq.produced", 1L, "reason=Undeserializable"),
            ],
            measurements);
    }
}
