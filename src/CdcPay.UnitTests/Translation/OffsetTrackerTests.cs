using CdcPay.Translation.Assembly;
using CdcPay.Translation.Raw;

namespace CdcPay.UnitTests.Translation;

public sealed class OffsetTrackerTests
{
    private readonly OffsetTracker _tracker = new();

    [Fact]
    public void CommitPoints_WithNothingInProgress_IsNextOffset()
    {
        Handle("cdc.HrCore.dbo.Salary", 0, 1, 2);

        Assert.Equal(3, _tracker.CommitPoints()[("cdc.HrCore.dbo.Salary", 0)]);
    }

    [Fact]
    public void CommitPoints_WithMessageInProgress_StopsAtIt()
    {
        Handle("cdc.HrCore.dbo.Salary", 0, 1, 2);
        _tracker.Register(new KafkaPosition("cdc.HrCore.dbo.Salary", 0, 3));
        Handle("cdc.HrCore.dbo.Salary", 4, 5);

        Assert.Equal(3, _tracker.CommitPoints()[("cdc.HrCore.dbo.Salary", 0)]);

        _tracker.Complete(new KafkaPosition("cdc.HrCore.dbo.Salary", 0, 3));
        Assert.Equal(6, _tracker.CommitPoints()[("cdc.HrCore.dbo.Salary", 0)]);
    }

    [Fact]
    public void CommitPoints_TrackPartitionsIndependently()
    {
        _tracker.Register(new KafkaPosition("cdc.transaction", 0, 7));
        Handle("cdc.HrCore.dbo.Salary", 10);

        IReadOnlyDictionary<(string Topic, int Partition), long> points = _tracker.CommitPoints();

        Assert.Equal(7, points[("cdc.transaction", 0)]);
        Assert.Equal(11, points[("cdc.HrCore.dbo.Salary", 0)]);
    }

    private void Handle(string topic, params long[] offsets)
    {
        foreach (long offset in offsets)
        {
            KafkaPosition position = new(topic, 0, offset);
            _tracker.Register(position);
            _tracker.Complete(position);
        }
    }
}
