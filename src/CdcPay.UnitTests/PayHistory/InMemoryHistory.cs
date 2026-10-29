using CdcPay.PayHistory;

namespace CdcPay.UnitTests.PayHistory;

/// <summary>Executes merge plans the way the store does, so tests read as a history.</summary>
internal sealed class InMemoryHistory
{
    public const int Tenant = 1;
    public const int Employee = 1001;

    private readonly List<PayHistoryRow> _rows = [];
    private long _nextId = 1;

    public IReadOnlyList<PayHistoryRow> Rows => _rows;

    public IReadOnlyList<PayHistoryRow> Current =>
        [.. _rows.Where(row => row.RecordedTo is null).OrderBy(row => row.PayComponent, StringComparer.Ordinal).ThenBy(row => row.ValidFrom)];

    public static DateTimeOffset Instant(int month, int day, int hour = 12) => new(2026, month, day, hour, 0, 0, TimeSpan.Zero);

    public static DateOnly Day(int year, int month, int day = 1) => new(year, month, day);

    public static SalaryFact Salary(
        decimal amount, DateOnly from, DateOnly? to, DateTimeOffset recordedAt, int salaryId, string component = "Base", bool correction = false) =>
        new(Tenant, Employee, recordedAt, Guid.NewGuid(), component, salaryId, amount, "SEK", from, to, correction);

    public static TerminationFact Termination(DateOnly payEndsOn, DateTimeOffset recordedAt) =>
        new(Tenant, Employee, recordedAt, Guid.NewGuid(), payEndsOn);

    public MergePlan Apply(PayFact fact)
    {
        MergePlan plan = PayHistoryMerge.Apply([.. _rows.Where(row => row.RecordedTo is null)], fact);
        for (int i = 0; i < _rows.Count; i++)
        {
            if (plan.Close.Contains(_rows[i].PayHistoryId))
            {
                _rows[i] = _rows[i] with { RecordedTo = plan.At };
            }
        }

        _ = _rows.RemoveAll(row => plan.Delete.Contains(row.PayHistoryId));
        _rows.AddRange(plan.Insert.Select(row => row with { PayHistoryId = _nextId++ }));
        return plan;
    }

    /// <summary>The amount on <paramref name="validAt"/> as known at <paramref name="knownAt"/> (null: now).</summary>
    public decimal? AmountAt(DateOnly validAt, DateTimeOffset? knownAt = null, string component = "Base") =>
        _rows.SingleOrDefault(row =>
            row.PayComponent == component
            && row.ValidFrom <= validAt && (row.ValidTo is null || validAt < row.ValidTo)
            && (knownAt is not { } known
                ? row.RecordedTo is null
                : row.RecordedFrom <= known && (row.RecordedTo is null || known < row.RecordedTo)))?.MonthlyAmount;
}
