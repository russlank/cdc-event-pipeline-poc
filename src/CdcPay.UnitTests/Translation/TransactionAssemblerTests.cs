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
    public void ExpireStale_WithRowsButNoEndAfterLaterTransaction_AbandonsOrphans()
    {
        RawChange orphan = Create(Salary(200, 30000m, Nov), "t1", 1);
        _assembler.Accept(orphan);
        _clock.Advance(TimeSpan.FromSeconds(30));
        _assembler.Accept(Begin("t2"));

        _clock.Advance(TimeSpan.FromSeconds(59));
        _assembler.ExpireStale();
        Assert.All(_assembler.Drain(), output => Assert.IsType<Discarded>(output));

        _clock.Advance(TimeSpan.FromSeconds(1));
        _assembler.ExpireStale();

        AbandonedTransaction abandoned = Assert.IsType<AbandonedTransaction>(Assert.Single(_assembler.Drain()));
        Assert.Equal([orphan], abandoned.Rows);
        Assert.Null(abandoned.End);
    }

    [Fact]
    public void ExpireStale_WithRowsButNoEndAndNothingLater_KeepsWaiting()
    {
        // Debezium emits the END when the next transaction begins; while Connect is down, none does.
        RawChange raise = Create(Salary(200, 30000m, Nov), "t1", 1);
        _assembler.Accept(raise);

        _clock.Advance(TimeSpan.FromMinutes(30));
        _assembler.ExpireStale();
        Assert.Empty(_assembler.Drain());

        _assembler.Accept(End("t1", raise));

        Assert.Equal([raise], Assert.IsType<CompletedTransaction>(Assert.Single(_assembler.Drain())).Rows);
    }

    [Fact]
    public void ExpireStale_WithEarlierTransactionArrivingLate_KeepsWaiting()
    {
        // Observed when Connect stopped right after a raise: the heartbeat row of the transaction
        // before it arrived after the raise's BEGIN and rows. That is not a later transaction.
        RawChange heartbeat = Update(Other("DebeziumHeartbeat"), Other("DebeziumHeartbeat"), "t1", 1);
        RawChange raise = Create(Salary(200, 30000m, Nov), "t2", 1);
        _assembler.Accept(Begin("t1"));
        _assembler.Accept(End("t1", heartbeat));
        _assembler.Accept(Begin("t2"));
        _assembler.Accept(raise);
        _assembler.Accept(heartbeat);
        _assembler.Accept(Begin("t1"));

        _clock.Advance(TimeSpan.FromMinutes(5));
        _assembler.ExpireStale();

        Assert.Empty(_assembler.Drain().OfType<AbandonedTransaction>());
        Assert.Equal(1, _assembler.OpenTransactionCount);
    }

    [Fact]
    public void Accept_WithChangesResentByDebezium_CountsThemTowardEndOnce()
    {
        // Observed after a Connect restart: Debezium sends the rows of the open transaction again
        // with the next total orders, and its END counts both sends.
        RawChange close = Update(Salary(113, 37389m, May), Salary(113, 37389m, May, Nov), "t1", 1);
        RawChange raise = Create(Salary(114, 38137m, Nov), "t1", 2);
        RawChange closeAgain = Resent(close, 3);
        RawChange raiseAgain = Resent(raise, 4);

        _assembler.Accept(close);
        _assembler.Accept(raise);
        _assembler.Accept(closeAgain);
        _assembler.Accept(raiseAgain);
        _assembler.Accept(End("t1", close, raise, closeAgain, raiseAgain));

        IReadOnlyList<AssemblerOutput> outputs = _assembler.Drain();
        Assert.Equal(
            [closeAgain.Message.Position, raiseAgain.Message.Position],
            outputs.OfType<Discarded>().Select(discarded => discarded.Position));
        Assert.Equal([close, raise], Assert.Single(outputs.OfType<CompletedTransaction>()).Rows);
        Assert.Equal(0, _assembler.OpenTransactionCount);
    }

    [Fact]
    public void Accept_WithChangesResentAfterRelease_DiscardsThem()
    {
        RawChange raise = Create(Salary(114, 38137m, Nov), "t1", 1);
        _assembler.Accept(raise);
        _assembler.Accept(End("t1", raise));
        _ = Assert.IsType<CompletedTransaction>(Assert.Single(_assembler.Drain()));

        _assembler.Accept(Resent(raise, 2));

        _ = Assert.IsType<Discarded>(Assert.Single(_assembler.Drain()));
    }

    [Fact]
    public void Accept_WithRedeliveredTransaction_DiscardsTheDuplicates()
    {
        RawChange row = Create(Salary(200, 30000m, Nov), "t1", 1);
        TransactionMarker end = End("t1", row);
        _assembler.Accept(row);
        _assembler.Accept(end);
        _ = Assert.IsType<CompletedTransaction>(Assert.Single(_assembler.Drain()));

        // A Debezium task restart sends the same transaction again.
        _assembler.Accept(row);
        _assembler.Accept(end);

        Assert.All(_assembler.Drain(), output => Assert.IsType<Discarded>(output));
        Assert.Equal(0, _assembler.OpenTransactionCount);
    }

    [Fact]
    public void Accept_WithDuplicateRowBeforeBothEnds_DoesNotBlockLaterTransactions()
    {
        // Observed after a Debezium task restart: row, duplicate row, END, duplicate END.
        // The duplicate END must not reopen the transaction and block the queue.
        RawChange heartbeat = Update(Other("DebeziumHeartbeat"), Other("DebeziumHeartbeat"), "hb", 1);
        RawChange duplicate = Update(Other("DebeziumHeartbeat"), Other("DebeziumHeartbeat"), "hb", 1);
        TransactionMarker end = End("hb", heartbeat);
        RawChange raise = Create(Salary(300, 30000m, Nov), "t2", 1);

        _assembler.Accept(heartbeat);
        _assembler.Accept(duplicate);
        _assembler.Accept(end);
        _assembler.Accept(end);
        _assembler.Accept(raise);
        _assembler.Accept(End("t2", raise));

        IReadOnlyList<AssemblerOutput> outputs = _assembler.Drain();
        Assert.Equal(["hb", "t2"], outputs.OfType<CompletedTransaction>().Select(t => t.TransactionId));
        Assert.Equal(2, outputs.OfType<Discarded>().Count());
        Assert.Equal(0, _assembler.OpenTransactionCount);
    }

    [Fact]
    public void Accept_WithRowThatReleasedTransactionDidNotHave_AbandonsIt()
    {
        RawChange row = Create(Salary(200, 30000m, Nov), "t1", 1);
        _assembler.Accept(End("t1", row));
        _assembler.Accept(row);
        _ = _assembler.Drain();

        RawChange unknown = Create(Salary(201, 30000m, Nov), "t1", 2);
        _assembler.Accept(unknown);

        Assert.Equal([unknown], Assert.IsType<AbandonedTransaction>(Assert.Single(_assembler.Drain())).Rows);
    }

    [Fact]
    public void Accept_WithMissingRowAfterTimeout_AbandonsTheLateRow()
    {
        RawChange arrived = Create(Salary(200, 30000m, Nov), "t1", 1);
        RawChange missing = Create(Salary(201, 30000m, Nov), "t1", 2);
        _assembler.Accept(End("t1", arrived, missing));
        _assembler.Accept(arrived);
        _clock.Advance(TimeSpan.FromSeconds(60));
        _assembler.ExpireStale();
        _ = Assert.IsType<AbandonedTransaction>(Assert.Single(_assembler.Drain()));

        _assembler.Accept(missing);

        Assert.Equal([missing], Assert.IsType<AbandonedTransaction>(Assert.Single(_assembler.Drain())).Rows);
    }

    [Fact]
    public void Accept_WithRowRepeatedInOpenTransaction_DiscardsTheRepeat()
    {
        RawChange first = Create(Salary(200, 30000m, Nov), "t1", 1);
        RawChange second = Create(Position(10, "Lead", 4, Nov), "t1", 2);
        RawChange repeat = Create(Salary(200, 30000m, Nov), "t1", 1);

        _assembler.Accept(first);
        _assembler.Accept(repeat);
        _assembler.Accept(second);
        _assembler.Accept(End("t1", first, second));

        IReadOnlyList<AssemblerOutput> outputs = _assembler.Drain();
        Assert.Equal(repeat.Message.Position, Assert.IsType<Discarded>(outputs[0]).Position);
        Assert.Equal(2, Assert.IsType<CompletedTransaction>(outputs[1]).Rows.Count);
    }

    [Fact]
    public void Accept_WithBeginOrRepeatedEnd_Discards()
    {
        RawChange row = Create(Salary(200, 30000m, Nov), "t1", 1);
        RawChange other = Create(Salary(201, 30000m, Nov), "t1", 2);
        TransactionMarker end = End("t1", row, other);
        _assembler.Accept(Begin("t1"));
        _assembler.Accept(end);
        _assembler.Accept(row);
        _assembler.Accept(end);
        _assembler.Accept(other);

        IReadOnlyList<AssemblerOutput> outputs = _assembler.Drain();
        Assert.Equal(3, outputs.Count);
        _ = Assert.IsType<Discarded>(outputs[0]);
        _ = Assert.IsType<Discarded>(outputs[1]);
        _ = Assert.IsType<CompletedTransaction>(outputs[2]);
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
