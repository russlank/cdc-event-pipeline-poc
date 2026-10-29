using CdcPay.Contracts;
using CdcPay.Contracts.Events;
using CdcPay.Translation.Assembly;
using CdcPay.Translation.Raw;

namespace CdcPay.Translation.Classification;

/// <summary>
/// Turns complete source transactions and snapshot rows into business events.
/// Rows are grouped per employee; each group must match one known shape (hire, raise,
/// promotion, correction, position change, termination), or it goes to the dead-letter topic.
/// Nothing is dropped silently: rows without pay meaning are returned as ignored with a reason.
/// </summary>
public sealed class EventClassifier
{
    private static readonly HashSet<string> SalaryReasons = new(StringComparer.Ordinal) { "Hire", "Raise", "Promotion", "Correction" };
    private static readonly HashSet<string> ChangeReasons = new(StringComparer.Ordinal) { "Raise", "Promotion" };

    private readonly HashSet<string> _currencies;

    public EventClassifier(TranslationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _currencies = new HashSet<string>(options.SupportedCurrencies, StringComparer.Ordinal);
    }

    public IReadOnlyList<TranslationOutcome> Classify(CompletedTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        List<TranslationOutcome> outcomes = [];
        List<RawChange> payRows = [];
        List<RawChange> infrastructure = [];
        List<RawChange> referenceData = [];

        foreach (RawChange row in transaction.Rows)
        {
            switch (row.Row)
            {
                case OtherRow { Table: "DebeziumHeartbeat" or "DebeziumSignal" }:
                    infrastructure.Add(row);
                    break;
                case OtherRow { Table: "Tenant" }:
                    referenceData.Add(row);
                    break;
                case OtherRow other:
                    outcomes.Add(DeadLetter(DeadLetterReasons.UnsupportedChange, $"table dbo.{other.Table} is not translated", [row]));
                    break;
                default:
                    payRows.Add(row);
                    break;
            }
        }

        if (infrastructure.Count > 0)
        {
            outcomes.Add(new IgnoredOutcome(IgnoreReasons.Infrastructure, infrastructure));
        }

        if (referenceData.Count > 0)
        {
            outcomes.Add(new IgnoredOutcome(IgnoreReasons.ReferenceData, referenceData));
        }

        foreach (IGrouping<(int, int), RawChange> employee in payRows.GroupBy(EmployeeOf))
        {
            outcomes.AddRange(ClassifyEmployeeChanges([.. employee]));
        }

        return outcomes;
    }

    public IReadOnlyList<TranslationOutcome> Classify(SnapshotRow snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        RawChange row = snapshot.Row;
        if (row.Row is OtherRow other)
        {
            string reason = other.Table == "Tenant" ? IgnoreReasons.ReferenceData : IgnoreReasons.Infrastructure;
            return [new IgnoredOutcome(reason, [row])];
        }

        if (Invalid(row) is { } problem)
        {
            return [DeadLetter(DeadLetterReasons.InvalidRow, problem, [row])];
        }

        return row.Row switch
        {
            EmployeeRow employee when employee.TerminationDate is { } terminated =>
                [Event(Hired(row, employee, null, null), [row]), Event(Terminated(row, employee, terminated), [row])],
            EmployeeRow employee => [Event(Hired(row, employee, null, null), [row])],
            SalaryRow salary => [Event(SalaryChanged(row, salary, null), [row])],
            PositionRow position => [Event(PositionChanged(row, position, null), [row])],
            _ => [DeadLetter(DeadLetterReasons.UnsupportedChange, "unknown row type", [row])],
        };
    }

    private IEnumerable<TranslationOutcome> ClassifyEmployeeChanges(IReadOnlyList<RawChange> rows)
    {
        if (rows.FirstOrDefault(row => row.Operation == ChangeOperation.Delete) is { } deleted)
        {
            return [DeadLetter(DeadLetterReasons.UnsupportedChange, $"delete on {deleted.QualifiedTable}", rows)];
        }

        if (rows.Any(row => row.Before is not null && row.After is not null && EmployeeOf(row.Before) != EmployeeOf(row.After)))
        {
            return [DeadLetter(DeadLetterReasons.UnsupportedChange, "row moved to another tenant or employee", rows)];
        }

        if (rows.Select(Invalid).FirstOrDefault(problem => problem is not null) is { } invalid)
        {
            return [DeadLetter(DeadLetterReasons.InvalidRow, invalid, rows)];
        }

        List<RawChange> employees = [.. rows.Where(row => row.Row is EmployeeRow)];
        List<RawChange> salaryInserts = [.. rows.Where(row => row is { Row: SalaryRow, Operation: ChangeOperation.Create })];
        List<RawChange> positionInserts = [.. rows.Where(row => row is { Row: PositionRow, Operation: ChangeOperation.Create })];
        List<RawChange> salaryUpdates = [.. rows.Where(row => row is { Row: SalaryRow, Operation: ChangeOperation.Update })];
        List<RawChange> positionUpdates = [.. rows.Where(row => row is { Row: PositionRow, Operation: ChangeOperation.Update })];
        List<RawChange> closingSalaries = [.. salaryUpdates.Where(IsClosingUpdate)];
        List<RawChange> closingPositions = [.. positionUpdates.Where(IsClosingUpdate)];
        List<RawChange> otherSalaryUpdates = [.. salaryUpdates.Except(closingSalaries)];
        List<RawChange> otherPositionUpdates = [.. positionUpdates.Except(closingPositions)];

        if (employees.Count > 1)
        {
            return [Unclassified("more than one Employee change for one employee", rows)];
        }

        if (employees.Count == 1)
        {
            RawChange change = employees[0];
            return change.Operation == ChangeOperation.Create
                ? ClassifyHire(rows, change, salaryInserts, positionInserts)
                : ClassifyEmployeeUpdate(rows, change, salaryInserts.Count + positionInserts.Count + otherSalaryUpdates.Count + otherPositionUpdates.Count);
        }

        RawChange? correction = otherSalaryUpdates.FirstOrDefault(row => ((SalaryRow)row.After!).Reason == "Correction");
        if (correction is not null)
        {
            return rows.Count == 1
                ? [ClassifyCorrection(correction)]
                : [DeadLetter(DeadLetterReasons.InvalidCorrection, "a correction must be the only change in its transaction", rows)];
        }

        if (otherSalaryUpdates.Count > 0 || otherPositionUpdates.Count > 0)
        {
            return [Unclassified("update that is neither a correction nor the closing of a period", rows)];
        }

        if (salaryInserts.Count == 1 && positionInserts.Count <= 1)
        {
            RawChange insert = salaryInserts[0];
            var salary = (SalaryRow)insert.After!;
            if (!ChangeReasons.Contains(salary.Reason))
            {
                return [Unclassified($"new salary row with reason {salary.Reason} outside a hire", rows)];
            }

            decimal? previous = closingSalaries.Count == 1 ? ((SalaryRow)closingSalaries[0].Before!).MonthlyAmount : null;
            List<TranslationOutcome> outcomes = [Event(SalaryChanged(insert, salary, previous), rows)];
            if (positionInserts.Count == 1)
            {
                outcomes.Add(Event(PositionChanged(positionInserts[0], (PositionRow)positionInserts[0].After!, PreviousPosition(closingPositions)), rows));
            }

            return outcomes;
        }

        if (salaryInserts.Count == 0 && positionInserts.Count == 1)
        {
            return [Event(PositionChanged(positionInserts[0], (PositionRow)positionInserts[0].After!, PreviousPosition(closingPositions)), rows)];
        }

        return [Unclassified("no known business shape", rows)];
    }

    private static IEnumerable<TranslationOutcome> ClassifyHire(
        IReadOnlyList<RawChange> rows, RawChange employeeInsert, List<RawChange> salaryInserts, List<RawChange> positionInserts)
    {
        bool onlyInserts = rows.All(row => row.Operation == ChangeOperation.Create);
        if (!onlyInserts || salaryInserts.Count > 1 || positionInserts.Count > 1
            || salaryInserts.Any(row => ((SalaryRow)row.After!).Reason != "Hire"))
        {
            return [Unclassified("hire must insert one Employee with at most one Salary (Hire) and one Position", rows)];
        }

        var employee = (EmployeeRow)employeeInsert.After!;
        InitialSalary? salary = salaryInserts.Count == 1 && salaryInserts[0].After is SalaryRow s
            ? new InitialSalary { salaryId = s.SalaryId, monthlyAmount = PayEventFactory.ToAvroDecimal(s.MonthlyAmount), currency = s.Currency }
            : null;
        InitialPosition? position = positionInserts.Count == 1 && positionInserts[0].After is PositionRow p
            ? new InitialPosition { positionId = p.PositionId, title = p.Title, level = p.Level }
            : null;
        return [Event(Hired(employeeInsert, employee, salary, position, rows), rows)];
    }

    private static IEnumerable<TranslationOutcome> ClassifyEmployeeUpdate(IReadOnlyList<RawChange> rows, RawChange change, int otherPayChanges)
    {
        var before = (EmployeeRow)change.Before!;
        var after = (EmployeeRow)change.After!;

        bool terminated = before.TerminationDate is null && after.TerminationDate is not null;
        bool onlyTermination = after with { TerminationDate = null } == before;
        if (terminated && onlyTermination && otherPayChanges == 0)
        {
            return [Event(Terminated(change, after, after.TerminationDate!.Value, rows), rows)];
        }

        bool onlyNameOrGender = after with { Name = before.Name, Gender = before.Gender } == before;
        if (onlyNameOrGender && rows.Count == 1)
        {
            return [new IgnoredOutcome(after.Gender != before.Gender ? IgnoreReasons.GenderChange : IgnoreReasons.NonPayChange, rows)];
        }

        return [Unclassified("Employee update that is neither a termination nor a non-pay change", rows)];
    }

    private static TranslationOutcome ClassifyCorrection(RawChange correction)
    {
        var before = (SalaryRow)correction.Before!;
        var after = (SalaryRow)correction.After!;

        bool otherColumnsUnchanged = after with
        {
            MonthlyAmount = before.MonthlyAmount,
            EffectiveFrom = before.EffectiveFrom,
            EffectiveTo = before.EffectiveTo,
            Reason = before.Reason,
        } == before;
        if (!otherColumnsUnchanged)
        {
            return DeadLetter(DeadLetterReasons.InvalidCorrection, "a correction may change only MonthlyAmount, EffectiveFrom, and EffectiveTo", [correction]);
        }

        List<string> changed = [];
        if (after.MonthlyAmount != before.MonthlyAmount)
        {
            changed.Add("MonthlyAmount");
        }

        if (after.EffectiveFrom != before.EffectiveFrom)
        {
            changed.Add("EffectiveFrom");
        }

        if (after.EffectiveTo != before.EffectiveTo)
        {
            changed.Add("EffectiveTo");
        }

        if (changed.Count == 0)
        {
            return DeadLetter(DeadLetterReasons.InvalidCorrection, "a correction must change MonthlyAmount, EffectiveFrom, or EffectiveTo", [correction]);
        }

        SalaryCorrectedPayload payload = new()
        {
            salaryId = after.SalaryId,
            monthlyAmount = PayEventFactory.ToAvroDecimal(after.MonthlyAmount),
            currency = after.Currency,
            changedFields = changed,
            previousMonthlyAmount = changed.Contains("MonthlyAmount") ? PayEventFactory.ToAvroDecimal(before.MonthlyAmount) : null,
            previousEffectiveFrom = changed.Contains("EffectiveFrom") ? PayEventFactory.ToAvroDate(before.EffectiveFrom) : null,
            previousEffectiveTo = changed.Contains("EffectiveTo") && before.EffectiveTo is { } to ? PayEventFactory.ToAvroDate(to) : null,
        };
        PayEvent @event = PayEventFactory.Create(
            EventTypes.SalaryCorrected, correction, [correction], after.TenantId, after.EmployeeId, after.EffectiveFrom, after.EffectiveTo, payload);
        return Event(@event, [correction]);
    }

    private static PayEvent Hired(RawChange anchor, EmployeeRow employee, InitialSalary? salary, InitialPosition? position, IEnumerable<RawChange>? rows = null) =>
        PayEventFactory.Create(
            EventTypes.EmployeeHired, anchor, rows ?? [anchor], employee.TenantId, employee.EmployeeId, employee.HireDate, null,
            new EmployeeHiredPayload
            {
                hireDate = PayEventFactory.ToAvroDate(employee.HireDate),
                gender = employee.Gender,
                initialSalary = salary,
                initialPosition = position,
            });

    private static PayEvent Terminated(RawChange anchor, EmployeeRow employee, DateOnly terminationDate, IEnumerable<RawChange>? rows = null) =>
        PayEventFactory.Create(
            EventTypes.EmployeeTerminated, anchor, rows ?? [anchor], employee.TenantId, employee.EmployeeId, terminationDate, null,
            new EmployeeTerminatedPayload { terminationDate = PayEventFactory.ToAvroDate(terminationDate) });

    private static PayEvent SalaryChanged(RawChange anchor, SalaryRow salary, decimal? previousAmount) =>
        PayEventFactory.Create(
            EventTypes.SalaryChanged, anchor, [anchor], salary.TenantId, salary.EmployeeId, salary.EffectiveFrom, salary.EffectiveTo,
            new SalaryChangedPayload
            {
                salaryId = salary.SalaryId,
                monthlyAmount = PayEventFactory.ToAvroDecimal(salary.MonthlyAmount),
                currency = salary.Currency,
                reason = salary.Reason,
                previousMonthlyAmount = previousAmount is { } amount ? PayEventFactory.ToAvroDecimal(amount) : null,
            });

    private static PayEvent PositionChanged(RawChange anchor, PositionRow position, PositionRow? previous) =>
        PayEventFactory.Create(
            EventTypes.PositionChanged, anchor, [anchor], position.TenantId, position.EmployeeId, position.EffectiveFrom, position.EffectiveTo,
            new PositionChangedPayload
            {
                positionId = position.PositionId,
                title = position.Title,
                level = position.Level,
                previousTitle = previous?.Title,
                previousLevel = previous?.Level,
            });

    private static PositionRow? PreviousPosition(List<RawChange> closingPositions) =>
        closingPositions.Count == 1 ? (PositionRow)closingPositions[0].Before! : null;

    /// <summary>An update that only sets or moves EffectiveTo: closing a period as part of a larger change.</summary>
    private static bool IsClosingUpdate(RawChange update) => (update.Before, update.After) switch
    {
        (SalaryRow before, SalaryRow after) => after.EffectiveTo != before.EffectiveTo && after with { EffectiveTo = before.EffectiveTo } == before,
        (PositionRow before, PositionRow after) => after.EffectiveTo != before.EffectiveTo && after with { EffectiveTo = before.EffectiveTo } == before,
        _ => false,
    };

    private string? Invalid(RawChange change) => change.After switch
    {
        SalaryRow s when !SalaryReasons.Contains(s.Reason) => $"Salary.Reason '{s.Reason}' is not one of Hire, Raise, Promotion, Correction",
        SalaryRow s when !_currencies.Contains(s.Currency) => $"Salary.Currency '{s.Currency}' is not supported",
        SalaryRow s when s.MonthlyAmount <= 0 => "Salary.MonthlyAmount must be positive",
        SalaryRow s when s.EffectiveTo <= s.EffectiveFrom => "Salary.EffectiveTo must be after EffectiveFrom",
        PositionRow p when p.Level < 1 => "Position.Level must be at least 1",
        PositionRow p when p.EffectiveTo <= p.EffectiveFrom => "Position.EffectiveTo must be after EffectiveFrom",
        EmployeeRow e when e.TerminationDate < e.HireDate => "Employee.TerminationDate must not be before HireDate",
        _ => null,
    };

    private static (int TenantId, int EmployeeId) EmployeeOf(RawChange change) => EmployeeOf(change.Row);

    private static (int TenantId, int EmployeeId) EmployeeOf(SourceRow row) => row switch
    {
        EmployeeRow e => (e.TenantId, e.EmployeeId),
        SalaryRow s => (s.TenantId, s.EmployeeId),
        PositionRow p => (p.TenantId, p.EmployeeId),
        _ => (0, 0),
    };

    private static EventOutcome Event(PayEvent @event, IReadOnlyList<RawChange> rows) => new(@event, rows);

    private static DeadLetterOutcome DeadLetter(string reason, string detail, IReadOnlyList<RawChange> rows) => new(reason, detail, rows);

    private static DeadLetterOutcome Unclassified(string detail, IReadOnlyList<RawChange> rows) =>
        DeadLetter(DeadLetterReasons.UnclassifiedTransaction, detail, rows);
}
