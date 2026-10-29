using CdcPay.PayHistory;
using static CdcPay.UnitTests.PayHistory.InMemoryHistory;

namespace CdcPay.UnitTests.PayHistory;

public sealed class PayHistoryMergeTests
{
    [Fact]
    public void Apply_FirstSalary_InsertsOneOpenRow()
    {
        InMemoryHistory history = new();

        _ = history.Apply(Salary(40000, Day(2025, 1), null, Instant(1, 10), salaryId: 1));

        PayHistoryRow row = Assert.Single(history.Rows);
        Assert.Equal((Day(2025, 1), null, Instant(1, 10), null), (row.ValidFrom, row.ValidTo, row.RecordedFrom, row.RecordedTo));
    }

    [Fact]
    public void Apply_ProspectiveRaise_SplitsTheOpenPeriodAtTheRaise()
    {
        InMemoryHistory history = new();
        _ = history.Apply(Salary(40000, Day(2025, 1), null, Instant(1, 10), salaryId: 1));

        _ = history.Apply(Salary(41200, Day(2026, 11), null, Instant(10, 29), salaryId: 2));

        Assert.Equal(
            [(Day(2025, 1), Day(2026, 11), 40000m), (Day(2026, 11), null, 41200m)],
            history.Current.Select(row => (row.ValidFrom, row.ValidTo, row.MonthlyAmount)));
        PayHistoryRow closed = Assert.Single(history.Rows, row => row.RecordedTo is not null);
        Assert.Equal((Day(2025, 1), null, (DateTimeOffset?)Instant(10, 29)), (closed.ValidFrom, closed.ValidTo, closed.RecordedTo));
    }

    [Fact]
    public void Apply_RetroactiveRaise_ChangesThePastAndKeepsWhatWasBelievedBefore()
    {
        InMemoryHistory history = new();
        _ = history.Apply(Salary(40000, Day(2025, 1), null, Instant(1, 10), salaryId: 1));

        _ = history.Apply(Salary(42000, Day(2026, 7), null, Instant(10, 29), salaryId: 2));

        Assert.Equal(42000m, history.AmountAt(Day(2026, 8)));
        Assert.Equal(40000m, history.AmountAt(Day(2026, 8), knownAt: Instant(10, 28)));
        Assert.Equal(40000m, history.AmountAt(Day(2026, 6)));
    }

    [Fact]
    public void Apply_PeriodInsideAnOpenPeriod_SplitsItInThree()
    {
        InMemoryHistory history = new();
        _ = history.Apply(Salary(40000, Day(2025, 1), null, Instant(1, 10), salaryId: 1));

        _ = history.Apply(Salary(45000, Day(2026, 3), Day(2026, 6), Instant(10, 29), salaryId: 2));

        Assert.Equal(
            [(Day(2025, 1), Day(2026, 3), 40000m, 1), (Day(2026, 3), Day(2026, 6), 45000m, 2), (Day(2026, 6), null, 40000m, 1)],
            history.Current.Select(row => (row.ValidFrom, row.ValidTo, row.MonthlyAmount, row.SalaryId)));
    }

    [Fact]
    public void Apply_CorrectionOfTheAmount_RestatesThePeriodAndKeepsTheEarlierVersion()
    {
        InMemoryHistory history = new();
        _ = history.Apply(Salary(40000, Day(2025, 1), Day(2026, 4), Instant(1, 10), salaryId: 1));
        _ = history.Apply(Salary(41000, Day(2026, 4), null, Instant(1, 10), salaryId: 2));

        _ = history.Apply(Salary(41500, Day(2026, 4), null, Instant(10, 29), salaryId: 2, correction: true));

        Assert.Equal(41500m, history.AmountAt(Day(2026, 5)));
        Assert.Equal(41000m, history.AmountAt(Day(2026, 5), knownAt: Instant(10, 28)));
        Assert.Equal(40000m, history.AmountAt(Day(2026, 3)));
        Assert.Equal(2, history.Rows.Count(row => row.SalaryId == 2));
    }

    [Fact]
    public void Apply_CorrectionThatShortensThePeriod_LeavesNoStaleValue()
    {
        InMemoryHistory history = new();
        _ = history.Apply(Salary(40000, Day(2025, 1), Day(2026, 4), Instant(1, 10), salaryId: 1));
        _ = history.Apply(Salary(41000, Day(2026, 4), null, Instant(1, 10), salaryId: 2));

        _ = history.Apply(Salary(41000, Day(2026, 5), null, Instant(10, 29), salaryId: 2, correction: true));

        Assert.Null(history.AmountAt(Day(2026, 4, 15)));
        Assert.Equal(41000m, history.AmountAt(Day(2026, 4, 15), knownAt: Instant(10, 28)));
        Assert.Equal(41000m, history.AmountAt(Day(2026, 5)));
    }

    [Fact]
    public void Apply_CorrectionThatExtendsThePeriod_SplitsTheRowItNowOverlaps()
    {
        InMemoryHistory history = new();
        _ = history.Apply(Salary(40000, Day(2025, 1), Day(2026, 4), Instant(1, 10), salaryId: 1));
        _ = history.Apply(Salary(41000, Day(2026, 4), null, Instant(1, 10), salaryId: 2));

        _ = history.Apply(Salary(41000, Day(2026, 2), null, Instant(10, 29), salaryId: 2, correction: true));

        Assert.Equal(
            [(Day(2025, 1), Day(2026, 2), 40000m, 1), (Day(2026, 2), null, 41000m, 2)],
            history.Current.Select(row => (row.ValidFrom, row.ValidTo, row.MonthlyAmount, row.SalaryId)));
        Assert.Equal(40000m, history.AmountAt(Day(2026, 3), knownAt: Instant(10, 28)));
    }

    [Fact]
    public void Apply_Termination_EndsEveryPayComponent()
    {
        InMemoryHistory history = new();
        _ = history.Apply(Salary(40000, Day(2025, 1), null, Instant(1, 10), salaryId: 1));
        _ = history.Apply(Salary(2000, Day(2026, 1), null, Instant(1, 10), salaryId: 2, component: "Allowance"));

        _ = history.Apply(Termination(Day(2026, 11), Instant(10, 29)));

        Assert.Equal(
            [("Allowance", Day(2026, 1), (DateOnly?)Day(2026, 11)), ("Base", Day(2025, 1), Day(2026, 11))],
            history.Current.Select(row => (row.PayComponent, row.ValidFrom, row.ValidTo)));
        Assert.Null(history.AmountAt(Day(2026, 11)));
        Assert.Equal(40000m, history.AmountAt(Day(2026, 11), knownAt: Instant(10, 28)));
    }

    [Fact]
    public void Apply_TerminationBeforeAPeriodStarts_RetractsThatPeriod()
    {
        InMemoryHistory history = new();
        _ = history.Apply(Salary(40000, Day(2025, 1), Day(2026, 12), Instant(1, 10), salaryId: 1));
        _ = history.Apply(Salary(42000, Day(2026, 12), null, Instant(1, 10), salaryId: 2));

        _ = history.Apply(Termination(Day(2026, 11), Instant(10, 29)));

        PayHistoryRow current = Assert.Single(history.Current);
        Assert.Equal((1, Day(2026, 11)), (current.SalaryId, current.ValidTo));
    }

    [Fact]
    public void Apply_TerminationOfClosedPeriods_IsNoOp()
    {
        InMemoryHistory history = new();
        _ = history.Apply(Salary(40000, Day(2025, 1), Day(2026, 10), Instant(1, 10), salaryId: 1));

        MergePlan plan = history.Apply(Termination(Day(2026, 10), Instant(10, 29)));

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void Apply_FactAlreadyStated_IsNoOp()
    {
        InMemoryHistory history = new();
        _ = history.Apply(Salary(40000, Day(2025, 1), Day(2026, 4), Instant(1, 10), salaryId: 1));
        _ = history.Apply(Salary(40000, Day(2026, 4), null, Instant(1, 10), salaryId: 2));

        MergePlan same = history.Apply(Salary(40000, Day(2025, 1), Day(2026, 4), Instant(10, 29), salaryId: 1));
        MergePlan spanning = history.Apply(Salary(40000, Day(2025, 6), null, Instant(10, 29), salaryId: 3));

        Assert.True(same.IsEmpty);
        Assert.True(spanning.IsEmpty);
        Assert.Equal(2, history.Rows.Count);
    }

    [Fact]
    public void Apply_SameAmountOverAGap_IsNotNoOp()
    {
        InMemoryHistory history = new();
        _ = history.Apply(Salary(40000, Day(2025, 1), Day(2025, 6), Instant(1, 10), salaryId: 1));

        MergePlan plan = history.Apply(Salary(40000, Day(2025, 1), Day(2025, 9), Instant(10, 29), salaryId: 1));

        Assert.False(plan.IsEmpty);
        Assert.Equal(Day(2025, 9), Assert.Single(history.Current).ValidTo);
    }

    [Fact]
    public void Apply_AnotherPayComponent_LeavesBasePayAlone()
    {
        InMemoryHistory history = new();
        _ = history.Apply(Salary(40000, Day(2025, 1), null, Instant(1, 10), salaryId: 1));

        MergePlan plan = history.Apply(Salary(2000, Day(2026, 1), null, Instant(10, 29), salaryId: 2, component: "Allowance"));

        Assert.Empty(plan.Close);
        Assert.Equal(40000m, history.AmountAt(Day(2026, 2)));
        Assert.Equal(2000m, history.AmountAt(Day(2026, 2), component: "Allowance"));
    }

    [Fact]
    public void Apply_FactRecordedBeforeTheRowItReplaces_NeverRunsRecordedTimeBackwards()
    {
        InMemoryHistory history = new();
        _ = history.Apply(Salary(40000, Day(2025, 1), null, Instant(1, 10, hour: 13), salaryId: 1));

        MergePlan plan = history.Apply(Salary(41000, Day(2026, 3), null, Instant(1, 10, hour: 12), salaryId: 2));

        Assert.Equal(Instant(1, 10, hour: 13), plan.At);
        Assert.All(history.Rows, row => Assert.True(row.RecordedTo is null || row.RecordedTo > row.RecordedFrom));
    }

    [Fact]
    public void Apply_TwoFactsAtTheSameInstant_DeletesTheVersionThatWasNeverVisible()
    {
        InMemoryHistory history = new();
        _ = history.Apply(Salary(40000, Day(2025, 1), null, Instant(1, 10), salaryId: 1));

        MergePlan plan = history.Apply(Salary(41000, Day(2026, 3), null, Instant(1, 10), salaryId: 2));

        Assert.Empty(plan.Close);
        Assert.Single(plan.Delete);
        Assert.All(history.Rows, row => Assert.Null(row.RecordedTo));
        Assert.Equal(2, history.Rows.Count);
    }

    [Fact]
    public void Apply_ReplayOfTheSameFacts_YieldsTheSameRows()
    {
        SalaryFact[] facts =
        [
            Salary(40000, Day(2025, 1), null, Instant(1, 10), salaryId: 1),
            Salary(42000, Day(2026, 7), null, Instant(10, 29), salaryId: 2),
            Salary(42500, Day(2026, 7), null, Instant(10, 30), salaryId: 2, correction: true),
        ];
        InMemoryHistory first = new();
        InMemoryHistory second = new();

        foreach (SalaryFact fact in facts)
        {
            _ = first.Apply(fact);
        }

        foreach (SalaryFact fact in facts)
        {
            _ = second.Apply(fact);
        }

        Assert.Equal(first.Rows, second.Rows);
    }

    [Fact]
    public void Apply_RowsOfAnotherEmployee_Throws()
    {
        PayHistoryRow other = new(1, Tenant, Employee + 1, "Base", 1, 40000, "SEK", Day(2025, 1), null, Instant(1, 10), null, Guid.NewGuid());

        _ = Assert.Throws<ArgumentException>(() => PayHistoryMerge.Apply([other], Salary(41000, Day(2026, 1), null, Instant(10, 29), salaryId: 2)));
    }
}
