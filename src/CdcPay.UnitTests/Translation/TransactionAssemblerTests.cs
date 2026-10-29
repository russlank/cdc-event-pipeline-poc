using CdcPay.Translation.Assembly;
using CdcPay.Translation.Raw;
using Microsoft.Extensions.Time.Testing;
using static CdcPay.UnitTests.Translation.Changes;

namespace CdcPay.UnitTests.Translation;

public sealed class TransactionAssemblerTests
{
    private static readonly DateOnly May = new(2026, 5, 1);
    private static readonly DateOnly Nov = new(2026, 11, 1);

    private readonly FakeTimeProvider _clock = new();
    private readonly TransactionAssembler _assembler;

    public TransactionAssemblerTests()
    {
        _assembler = new TransactionAssembler(_clock, TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Accept_WithRowsBeforeEnd_ReleasesOnEnd()
    {
        RawChange close = Update(Salary(113, 37389m, May), Salary(113, 37389m, May, Nov), "t1", 1);
        RawChange raise = Create(Salary(114, 38137m, Nov), "t1", 2);

        _assembler.Accept(raise);
        _assembler.Accept(close);
        Assert.Empty(_assembler.Drain());

        _assembler.Accept(End("t1", close, raise));

        CompletedTransaction completed = Assert.IsType<CompletedTransaction>(Assert.Single(_assembler.Drain()));
        Assert.Equal([close, raise], completed.Rows);
        Assert.Equal(0, _assembler.OpenTransactionCount);
    }

    [Fact]
    public void Accept_WithEndBeforeRows_WaitsForAllRows()
    {
        RawChange close = Update(Salary(113, 37389m, May), Salary(113, 37389m, May, Nov), "t1", 1);
        RawChange raise = Create(Salary(114, 38137m, Nov), "t1", 2);

        _assembler.Accept(End("t1", close, raise));
        _assembler.Accept(close);
        Assert.Empty(_assembler.Drain());

        _assembler.Accept(raise);
        _ = Assert.IsType<CompletedTransaction>(Assert.Single(_assembler.Drain()));
    }

    [Fact]
    public void Accept_WithLaterTransactionCompleteFirst_ReleasesInCommitOrder()
    {
        RawChange first = Create(Salary(200, 30000m, Nov), "t1", 1);
        RawChange second = Create(Salary(201, 31000m, Nov), "t2", 1);

        _assembler.Accept(End("t1", first));
        _assembler.Accept(End("t2", second));
        _assembler.Accept(second);
        Assert.Empty(_assembler.Drain());

        _assembler.Accept(first);

        IReadOnlyList<AssemblerOutput> outputs = _assembler.Drain();
        Assert.Equal(["t1", "t2"], outputs.Cast<CompletedTransaction>().Select(t => t.TransactionId));
    }

    [Fact]
    public void ExpireStale_WithIncompleteHead_AbandonsItAndReleasesTheNext()
    {
        RawChange missing = Create(Salary(200, 30000m, Nov), "t1", 1);
        RawChange other = Create(Salary(201, 31000m, Nov), "t2", 1);
        _assembler.Accept(End("t1", missing));
        _assembler.Accept(End("t2", other));
        _assembler.Accept(other);

        _clock.Advance(TimeSpan.FromSeconds(59));
        _assembler.ExpireStale();
        Assert.Empty(_assembler.Drain());

        _clock.Advance(TimeSpan.FromSeconds(1));
        _assembler.ExpireStale();

        IReadOnlyList<AssemblerOutput> outputs = _assembler.Drain();
        AbandonedTransaction abandoned = Assert.IsType<AbandonedTransaction>(outputs[0]);
        Assert.Equal("t1", abandoned.TransactionId);
        Assert.Empty(abandoned.Rows);
        Assert.Equal("t2", Assert.IsType<CompletedTransaction>(outputs[1]).TransactionId);
    }

    [Fact]
    public void ExpireStale_WithRowsButNoEnd_AbandonsOrphans()
    {
        RawChange orphan = Create(Salary(200, 30000m, Nov), "t9", 1);
        _assembler.Accept(orphan);

        _clock.Advance(TimeSpan.FromSeconds(61));
        _assembler.ExpireStale();

        AbandonedTransaction abandoned = Assert.IsType<AbandonedTransaction>(Assert.Single(_assembler.Drain()));
        Assert.Equal([orphan], abandoned.Rows);
        Assert.Null(abandoned.End);
    }

    [Fact]
    public void Accept_WithRowOfClosedTransaction_AbandonsTheRow()
    {
        RawChange row = Create(Salary(200, 30000m, Nov), "t1", 1);
        _assembler.Accept(End("t1", row));
        _assembler.Accept(row);
        _ = _assembler.Drain();

        RawChange late = Create(Salary(201, 30000m, Nov), "t1", 2);
        _assembler.Accept(late);

        Assert.Equal([late], Assert.IsType<AbandonedTransaction>(Assert.Single(_assembler.Drain())).Rows);
    }

    [Fact]
    public void Accept_WithBeginOrRepeatedEnd_Discards()
    {
        RawChange row = Create(Salary(200, 30000m, Nov), "t1", 1);
        TransactionMarker end = End("t1", row);
        _assembler.Accept(Begin("t1"));
        _assembler.Accept(end);
        _assembler.Accept(row);
        _assembler.Accept(end);

        IReadOnlyList<AssemblerOutput> outputs = _assembler.Drain();
        Assert.Equal(3, outputs.Count);
        _ = Assert.IsType<Discarded>(outputs[0]);
        _ = Assert.IsType<CompletedTransaction>(outputs[1]);
        _ = Assert.IsType<Discarded>(outputs[2]);
    }

    [Fact]
    public void Accept_WithSnapshotRow_ReleasesImmediately()
    {
        RawChange snapshot = Snapshot(Salary(1, 42500m, May, reason: "Hire"));

        _assembler.Accept(snapshot);

        Assert.Same(snapshot, Assert.IsType<SnapshotRow>(Assert.Single(_assembler.Drain())).Row);
    }

    [Fact]
    public void Accept_WithFewerRowsOfOneTable_DoesNotComplete()
    {
        RawChange salary = Create(Salary(200, 30000m, Nov), "t1", 1);
        RawChange position = Create(Position(10, "Lead", 4, Nov), "t1", 2);
        RawChange otherSalary = Create(Salary(201, 30000m, Nov), "t1", 3);

        _assembler.Accept(End("t1", salary, position, otherSalary));
        _assembler.Accept(salary);
        _assembler.Accept(position);

        Assert.Empty(_assembler.Drain());
        Assert.Equal(1, _assembler.OpenTransactionCount);
    }

    [Fact]
    public void Positions_OfCompletedTransaction_IncludeRowsAndEnd()
    {
        RawChange row = Create(Salary(200, 30000m, Nov), "t1", 1);
        TransactionMarker end = End("t1", row);
        _assembler.Accept(row);
        _assembler.Accept(end);

        AssemblerOutput output = Assert.Single(_assembler.Drain());

        Assert.Equal([row.Message.Position, end.Message.Position], output.Positions);
    }
}
