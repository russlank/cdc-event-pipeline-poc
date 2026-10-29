using CdcPay.Contracts;
using CdcPay.Contracts.Events;
using CdcPay.Translation.Assembly;
using CdcPay.Translation.Classification;
using CdcPay.Translation.Raw;
using static CdcPay.UnitTests.Translation.Changes;

namespace CdcPay.UnitTests.Translation;

public sealed class EventClassifierTests
{
    private static readonly DateOnly Hired = new(2025, 8, 2);
    private static readonly DateOnly May = new(2026, 5, 1);
    private static readonly DateOnly Aug = new(2026, 8, 1);
    private static readonly DateOnly Nov = new(2026, 11, 1);

    private readonly EventClassifier _classifier = new(new TranslationOptions());

    [Fact]
    public void Classify_HireOfThreeTables_YieldsOneEmployeeHired()
    {
        RawChange employee = Create(Employee(1101, hired: Nov), order: 1);
        RawChange salary = Create(Salary(500, 36000m, Nov, reason: "Hire", employeeId: 1101), order: 2);
        RawChange position = Create(Position(80, "Associate", 1, Nov, employeeId: 1101), order: 3);

        PayEvent hired = SingleEvent(Transaction(employee, salary, position));

        Assert.Equal(EventTypes.EmployeeHired, hired.eventType);
        Assert.Equal(1101, hired.employeeId);
        EmployeeHiredPayload payload = Assert.IsType<EmployeeHiredPayload>(hired.payload);
        Assert.Equal("F", payload.gender);
        Assert.Equal(500, payload.initialSalary.salaryId);
        Assert.Equal(36000m, payload.initialSalary.monthlyAmount.ToType<decimal>());
        Assert.Equal("Associate", payload.initialPosition.title);
        Assert.Equal(["dbo.Employee", "dbo.Position", "dbo.Salary"], hired.source.tables);
        Assert.Equal(AvroDate(Nov), hired.effectiveFrom);
        Assert.Null(hired.effectiveTo);
    }

    [Fact]
    public void Classify_RaiseWithClosingUpdate_YieldsSalaryChangedWithPreviousAmount()
    {
        RawChange close = Update(Salary(113, 37389m, May), Salary(113, 37389m, May, Nov), order: 1);
        RawChange raise = Create(Salary(114, 38137m, Nov), order: 2);

        PayEvent changed = SingleEvent(Transaction(close, raise));

        Assert.Equal(EventTypes.SalaryChanged, changed.eventType);
        SalaryChangedPayload payload = Assert.IsType<SalaryChangedPayload>(changed.payload);
        Assert.Equal(114, payload.salaryId);
        Assert.Equal(38137m, payload.monthlyAmount.ToType<decimal>());
        Assert.Equal(37389m, payload.previousMonthlyAmount!.Value.ToType<decimal>());
        Assert.Equal("Raise", payload.reason);
        Assert.Equal("1:1001", PayEventContract.Key(changed.tenantId, changed.employeeId));
    }

    [Fact]
    public void Classify_RaiseEffectiveNextMonth_IsNotRetroactive()
    {
        PayEvent changed = SingleEvent(Transaction(Create(Salary(114, 38137m, Nov))));

        Assert.False(changed.isRetroactive);
    }

    [Fact]
    public void Classify_RaiseEffectiveThreeMonthsAgo_IsRetroactive()
    {
        PayEvent changed = SingleEvent(Transaction(Create(Salary(114, 38137m, Aug))));

        Assert.True(changed.isRetroactive);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(CommitMs).UtcDateTime, changed.recordedAt);
    }

    [Fact]
    public void Classify_Promotion_YieldsSalaryChangedAndPositionChangedInOneTransaction()
    {
        RawChange closeSalary = Update(Salary(113, 37389m, May), Salary(113, 37389m, May, Nov), order: 1);
        RawChange salary = Create(Salary(114, 40000m, Nov, reason: "Promotion"), order: 2);
        RawChange closePosition = Update(Position(9, "Specialist", 2, Hired), Position(9, "Specialist", 2, Hired, Nov), order: 3);
        RawChange position = Create(Position(10, "Senior Specialist", 3, Nov), order: 4);

        List<PayEvent> events = Events(Transaction(closeSalary, salary, closePosition, position));

        Assert.Equal([EventTypes.SalaryChanged, EventTypes.PositionChanged], events.Select(e => e.eventType));
        Assert.Equal(events[0].source.transactionId, events[1].source.transactionId);
        Assert.NotEqual(events[0].eventId, events[1].eventId);
        PositionChangedPayload payload = Assert.IsType<PositionChangedPayload>(events[1].payload);
        Assert.Equal("Specialist", payload.previousTitle);
        Assert.Equal(2, payload.previousLevel);
    }

    [Fact]
    public void Classify_CorrectionOfAmount_YieldsSalaryCorrected()
    {
        RawChange correction = Update(Salary(113, 37389m, May, reason: "Raise"), Salary(113, 37939m, May, reason: "Correction"));

        PayEvent corrected = SingleEvent(Transaction(correction));

        Assert.Equal(EventTypes.SalaryCorrected, corrected.eventType);
        SalaryCorrectedPayload payload = Assert.IsType<SalaryCorrectedPayload>(corrected.payload);
        Assert.Equal(["MonthlyAmount"], payload.changedFields);
        Assert.Equal(37389m, payload.previousMonthlyAmount!.Value.ToType<decimal>());
        Assert.Equal(37939m, payload.monthlyAmount.ToType<decimal>());
        Assert.Null(payload.previousEffectiveFrom);
    }

    [Fact]
    public void Classify_CorrectionOfPeriod_ReportsPreviousPeriod()
    {
        RawChange correction = Update(Salary(113, 37389m, May, Nov, reason: "Raise"), Salary(113, 37389m, Aug, null, reason: "Correction"));

        SalaryCorrectedPayload payload = Assert.IsType<SalaryCorrectedPayload>(SingleEvent(Transaction(correction)).payload);

        Assert.Equal(["EffectiveFrom", "EffectiveTo"], payload.changedFields);
        Assert.Equal(AvroDate(May), payload.previousEffectiveFrom);
        Assert.Equal(AvroDate(Nov), payload.previousEffectiveTo);
        Assert.Null(payload.previousMonthlyAmount);
    }

    [Fact]
    public void Classify_CorrectionOfCurrency_IsInvalidCorrection()
    {
        RawChange correction = Update(Salary(113, 37389m, May), Salary(113, 37389m, May, reason: "Correction", currency: "EUR"));

        AssertDeadLetter(Transaction(correction), DeadLetterReasons.InvalidCorrection);
    }

    [Fact]
    public void Classify_CorrectionThatChangesNothing_IsInvalidCorrection()
    {
        RawChange correction = Update(Salary(113, 37389m, May), Salary(113, 37389m, May, reason: "Correction"));

        AssertDeadLetter(Transaction(correction), DeadLetterReasons.InvalidCorrection);
    }

    [Fact]
    public void Classify_CorrectionWithOtherRows_IsInvalidCorrection()
    {
        RawChange correction = Update(Salary(113, 37389m, May), Salary(113, 30000m, May, reason: "Correction"), order: 1);
        RawChange raise = Create(Salary(114, 38137m, Nov), order: 2);

        AssertDeadLetter(Transaction(correction, raise), DeadLetterReasons.InvalidCorrection);
    }

    [Fact]
    public void Classify_PositionChangeAlone_YieldsPositionChanged()
    {
        RawChange close = Update(Position(9, "Specialist", 2, Hired), Position(9, "Specialist", 2, Hired, Nov), order: 1);
        RawChange position = Create(Position(10, "Lead", 4, Nov), order: 2);

        PayEvent changed = SingleEvent(Transaction(close, position));

        Assert.Equal(EventTypes.PositionChanged, changed.eventType);
        Assert.Equal(4, Assert.IsType<PositionChangedPayload>(changed.payload).level);
    }

    [Fact]
    public void Classify_TerminationWithClosedSalary_YieldsEmployeeTerminated()
    {
        DateOnly last = new(2026, 10, 31);
        RawChange employee = Update(Employee(), Employee(terminated: last), order: 1);
        RawChange close = Update(Salary(113, 37389m, May), Salary(113, 37389m, May, Nov), order: 2);

        PayEvent terminated = SingleEvent(Transaction(employee, close));

        Assert.Equal(EventTypes.EmployeeTerminated, terminated.eventType);
        Assert.Equal(AvroDate(last), Assert.IsType<EmployeeTerminatedPayload>(terminated.payload).terminationDate);
        Assert.Equal(AvroDate(last), terminated.effectiveFrom);
    }

    [Fact]
    public void Classify_NameChange_IsIgnoredAsNonPay()
    {
        RawChange rename = Update(Employee(), Employee() with { Name = "Employee 1001 (renamed)" });

        IgnoredOutcome ignored = Assert.IsType<IgnoredOutcome>(Assert.Single(_classifier.Classify(Transaction(rename))));
        Assert.Equal(IgnoreReasons.NonPayChange, ignored.Reason);
    }

    [Fact]
    public void Classify_GenderChange_IsIgnoredAsGenderChange()
    {
        RawChange change = Update(Employee(gender: "F"), Employee(gender: "X"));

        Assert.Equal(IgnoreReasons.GenderChange, Assert.IsType<IgnoredOutcome>(Assert.Single(_classifier.Classify(Transaction(change)))).Reason);
    }

    [Fact]
    public void Classify_DeleteOfSalary_IsUnsupportedChange()
    {
        AssertDeadLetter(Transaction(Delete(Salary(113, 37389m, May))), DeadLetterReasons.UnsupportedChange);
    }

    [Theory]
    [InlineData("Bonus", "SEK", 30000)]
    [InlineData("Raise", "XXX", 30000)]
    [InlineData("Raise", "SEK", -1)]
    [InlineData("Raise", "SEK", 0)]
    public void Classify_InvalidSalaryRow_IsInvalidRow(string reason, string currency, int amount)
    {
        RawChange invalid = Create(Salary(114, amount, Nov, reason: reason, currency: currency));

        DeadLetterOutcome deadLetter = AssertDeadLetter(Transaction(invalid), DeadLetterReasons.InvalidRow);
        Assert.DoesNotContain(amount.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".", deadLetter.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_PeriodEndingBeforeItStarts_IsInvalidRow()
    {
        AssertDeadLetter(Transaction(Create(Salary(114, 30000m, Nov, May))), DeadLetterReasons.InvalidRow);
    }

    [Fact]
    public void Classify_HeartbeatAndTenantRows_AreIgnoredWithReasons()
    {
        RawChange heartbeat = Update(Other("DebeziumHeartbeat"), Other("DebeziumHeartbeat"), order: 1);
        RawChange tenant = Create(Other("Tenant"), order: 2);

        IReadOnlyList<TranslationOutcome> outcomes = _classifier.Classify(Transaction(heartbeat, tenant));

        Assert.Equal([IgnoreReasons.Infrastructure, IgnoreReasons.ReferenceData], outcomes.Cast<IgnoredOutcome>().Select(o => o.Reason));
    }

    [Fact]
    public void Classify_UnknownTable_IsUnsupportedChange()
    {
        AssertDeadLetter(Transaction(Create(Other("Bonus"))), DeadLetterReasons.UnsupportedChange);
    }

    [Fact]
    public void Classify_LoneClosingUpdate_IsUnclassified()
    {
        AssertDeadLetter(Transaction(Update(Salary(113, 37389m, May), Salary(113, 37389m, May, Nov))), DeadLetterReasons.UnclassifiedTransaction);
    }

    [Fact]
    public void Classify_HireSalaryWithoutEmployee_IsUnclassified()
    {
        AssertDeadLetter(Transaction(Create(Salary(114, 30000m, Nov, reason: "Hire"))), DeadLetterReasons.UnclassifiedTransaction);
    }

    [Fact]
    public void Classify_TwoEmployeesInOneTransaction_YieldsOneEventEach()
    {
        RawChange first = Create(Salary(114, 30000m, Nov, employeeId: 1001), order: 1);
        RawChange second = Create(Salary(115, 31000m, Nov, employeeId: 1002), order: 2);

        List<PayEvent> events = Events(Transaction(first, second));

        Assert.Equal([1001, 1002], events.Select(e => e.employeeId));
    }

    [Fact]
    public void Classify_SameChangeTwice_YieldsSameEventId()
    {
        RawChange raise = Create(Salary(114, 38137m, Nov));

        Assert.Equal(SingleEvent(Transaction(raise)).eventId, SingleEvent(Transaction(raise)).eventId);
    }

    [Fact]
    public void Classify_SnapshotEmployee_YieldsHiredWithoutInitialRows()
    {
        PayEvent hired = Assert.IsType<EventOutcome>(Assert.Single(_classifier.Classify(new SnapshotRow(Snapshot(Employee()))))).Event;

        Assert.True(hired.source.snapshot);
        Assert.False(hired.isRetroactive);
        Assert.Null(hired.source.transactionId);
        EmployeeHiredPayload payload = Assert.IsType<EmployeeHiredPayload>(hired.payload);
        Assert.Null(payload.initialSalary);
        Assert.Null(payload.initialPosition);
    }

    [Fact]
    public void Classify_SnapshotOfTerminatedEmployee_YieldsHiredAndTerminated()
    {
        IReadOnlyList<TranslationOutcome> outcomes = _classifier.Classify(new SnapshotRow(Snapshot(Employee(terminated: new DateOnly(2026, 9, 30)))));

        Assert.Equal([EventTypes.EmployeeHired, EventTypes.EmployeeTerminated], outcomes.Cast<EventOutcome>().Select(o => o.Event.eventType));
    }

    [Fact]
    public void Classify_SnapshotSalary_IsNeverRetroactive()
    {
        PayEvent changed = Assert.IsType<EventOutcome>(Assert.Single(_classifier.Classify(new SnapshotRow(Snapshot(Salary(1, 42500m, Hired, reason: "Hire")))))).Event;

        Assert.Equal(EventTypes.SalaryChanged, changed.eventType);
        Assert.False(changed.isRetroactive);
        Assert.Equal("Hire", Assert.IsType<SalaryChangedPayload>(changed.payload).reason);
    }

    [Fact]
    public void Classify_SnapshotReadTwice_YieldsSameIdDespiteNewLsn()
    {
        SalaryRow row = Salary(1, 42500m, Hired, reason: "Hire");

        Guid first = SnapshotEvent(Snapshot(row, "lsn-a", readMs: CommitMs)).eventId;
        Guid second = SnapshotEvent(Snapshot(row, "lsn-b", readMs: CommitMs + 60_000)).eventId;
        Guid changedContent = SnapshotEvent(Snapshot(row with { MonthlyAmount = 43000m }, "lsn-b")).eventId;

        Assert.Equal(first, second);
        Assert.NotEqual(first, changedContent);
    }

    [Fact]
    public void Classify_SnapshotWithInvalidRow_IsInvalidRow()
    {
        TranslationOutcome outcome = Assert.Single(_classifier.Classify(new SnapshotRow(Snapshot(Salary(1, 42500m, Hired, currency: "XXX")))));

        Assert.Equal(DeadLetterReasons.InvalidRow, Assert.IsType<DeadLetterOutcome>(outcome).Reason);
    }

    [Fact]
    public void Classify_SnapshotTenant_IsIgnored()
    {
        Assert.Equal(IgnoreReasons.ReferenceData, Assert.IsType<IgnoredOutcome>(Assert.Single(_classifier.Classify(new SnapshotRow(Snapshot(Other("Tenant")))))).Reason);
    }

    private PayEvent SingleEvent(CompletedTransaction transaction) => Assert.Single(Events(transaction));

    private List<PayEvent> Events(CompletedTransaction transaction) =>
        [.. _classifier.Classify(transaction).Select(outcome => Assert.IsType<EventOutcome>(outcome).Event)];

    private PayEvent SnapshotEvent(RawChange row) =>
        Assert.IsType<EventOutcome>(Assert.Single(_classifier.Classify(new SnapshotRow(row)))).Event;

    private DeadLetterOutcome AssertDeadLetter(CompletedTransaction transaction, string reason)
    {
        DeadLetterOutcome deadLetter = Assert.IsType<DeadLetterOutcome>(Assert.Single(_classifier.Classify(transaction)));
        Assert.Equal(reason, deadLetter.Reason);
        Assert.Equal(transaction.Rows.Count, deadLetter.Rows.Count);
        return deadLetter;
    }

    private static DateTime AvroDate(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
}
