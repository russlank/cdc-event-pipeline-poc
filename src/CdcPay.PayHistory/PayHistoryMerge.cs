namespace CdcPay.PayHistory;

/// <summary>
/// The changes that apply one pay fact: rows to close (<c>recorded_to = At</c>), rows to delete,
/// and rows to insert (<c>recorded_from = At</c>). Empty when the fact changes nothing.
/// </summary>
public sealed record MergePlan(
    DateTimeOffset At,
    IReadOnlyList<long> Close,
    IReadOnlyList<long> Delete,
    IReadOnlyList<PayHistoryRow> Insert)
{
    public static MergePlan None { get; } = new(DateTimeOffset.MinValue, [], [], []);

    public bool IsEmpty => Close.Count == 0 && Delete.Count == 0 && Insert.Count == 0;
}

/// <summary>
/// The bitemporal merge: applies a pay fact to the current rows (<c>recorded_to</c> null) of one
/// employee. Pure: the result depends only on its inputs, so a replay of the same events in the
/// same order yields the same rows.
/// </summary>
public static class PayHistoryMerge
{
    /// <summary>Plans the fact against every current row of its employee (all pay components).</summary>
    public static MergePlan Apply(IReadOnlyList<PayHistoryRow> currentRows, PayFact fact)
    {
        ArgumentNullException.ThrowIfNull(currentRows);
        ArgumentNullException.ThrowIfNull(fact);

        if (currentRows.Any(row => row.TenantId != fact.TenantId || row.EmployeeId != fact.EmployeeId || row.RecordedTo is not null))
        {
            throw new ArgumentException("current rows must be the open rows of the fact's employee", nameof(currentRows));
        }

        return fact switch
        {
            SalaryFact salary => Merge(currentRows, salary),
            TerminationFact termination => Terminate(currentRows, termination),
            _ => throw new ArgumentException($"unknown fact {fact.GetType().Name}", nameof(fact)),
        };
    }

    /// <summary>
    /// A correction first retracts its salary row's current versions; they are closed and not
    /// inserted again, so a shortened period leaves no stale value. Then the fact replaces what is
    /// known for its valid period: overlapping rows are closed and their parts outside the period
    /// are inserted again. A fact already stated for its whole period is a no-op.
    /// </summary>
    private static MergePlan Merge(IReadOnlyList<PayHistoryRow> currentRows, SalaryFact fact)
    {
        List<PayHistoryRow> component = [.. currentRows.Where(row => row.PayComponent == fact.PayComponent)];
        List<PayHistoryRow> retracted = fact.IsCorrection ? [.. component.Where(row => row.SalaryId == fact.SalaryId)] : [];
        List<PayHistoryRow> overlapping = [.. component.Except(retracted).Where(row => row.Overlaps(fact.ValidFrom, fact.ValidTo))];

        if (retracted.Count == 0 && AlreadyStated(overlapping, fact))
        {
            return MergePlan.None;
        }

        List<PayHistoryRow> affected = [.. retracted, .. overlapping];
        DateTimeOffset at = RecordedAt(fact, affected);
        List<PayHistoryRow> insert = [];
        foreach (PayHistoryRow row in overlapping)
        {
            if (row.ValidFrom < fact.ValidFrom)
            {
                insert.Add(Reopen(row, at) with { ValidTo = fact.ValidFrom });
            }

            if (fact.ValidTo is { } end && (row.ValidTo is null || row.ValidTo > end))
            {
                insert.Add(Reopen(row, at) with { ValidFrom = end });
            }
        }

        insert.Add(new PayHistoryRow(
            0, fact.TenantId, fact.EmployeeId, fact.PayComponent, fact.SalaryId, fact.MonthlyAmount, fact.Currency,
            fact.ValidFrom, fact.ValidTo, at, null, fact.EventId));
        return Plan(at, affected, insert);
    }

    /// <summary>Every pay component ends at <see cref="TerminationFact.PayEndsOn"/>.</summary>
    private static MergePlan Terminate(IReadOnlyList<PayHistoryRow> currentRows, TerminationFact fact)
    {
        List<PayHistoryRow> affected = [.. currentRows.Where(row => row.ValidTo is null || row.ValidTo > fact.PayEndsOn)];
        if (affected.Count == 0)
        {
            return MergePlan.None;
        }

        DateTimeOffset at = RecordedAt(fact, affected);
        List<PayHistoryRow> insert =
        [
            .. affected.Where(row => row.ValidFrom < fact.PayEndsOn).Select(row => Reopen(row, at) with { ValidTo = fact.PayEndsOn }),
        ];
        return Plan(at, affected, insert);
    }

    /// <summary>The overlapping rows state the fact's amount and currency for every day of its period.</summary>
    private static bool AlreadyStated(List<PayHistoryRow> overlapping, SalaryFact fact)
    {
        if (overlapping.Any(row => row.MonthlyAmount != fact.MonthlyAmount || row.Currency != fact.Currency))
        {
            return false;
        }

        // Current rows of one component never overlap, so in order they either touch or leave a gap.
        DateOnly coveredUntil = fact.ValidFrom;
        foreach (PayHistoryRow row in overlapping.OrderBy(row => row.ValidFrom))
        {
            if (row.ValidFrom > coveredUntil)
            {
                return false;
            }

            if (row.ValidTo is not { } rowEnd)
            {
                return true;
            }

            coveredUntil = rowEnd;
        }

        return fact.ValidTo is { } end && coveredUntil >= end;
    }

    /// <summary>
    /// The event's recorded time, but never before the rows it replaces: a snapshot row can be read
    /// after a change that commits during the snapshot, and a recorded period must not run backwards.
    /// </summary>
    private static DateTimeOffset RecordedAt(PayFact fact, List<PayHistoryRow> affected) =>
        affected.Count == 0 ? fact.RecordedAt : new[] { fact.RecordedAt, affected.Max(row => row.RecordedFrom) }.Max();

    private static PayHistoryRow Reopen(PayHistoryRow row, DateTimeOffset at) =>
        row with { PayHistoryId = 0, RecordedFrom = at, RecordedTo = null };

    /// <summary>A row recorded at the same instant was never visible on its own; it is deleted, not closed.</summary>
    private static MergePlan Plan(DateTimeOffset at, List<PayHistoryRow> affected, List<PayHistoryRow> insert) => new(
        at,
        [.. affected.Where(row => row.RecordedFrom != at).Select(row => row.PayHistoryId)],
        [.. affected.Where(row => row.RecordedFrom == at).Select(row => row.PayHistoryId)],
        insert);
}
