using Avro;
using CdcPay.Contracts;
using CdcPay.Contracts.Events;
using CdcPay.PayHistory;

namespace CdcPay.UnitTests.PayHistory;

public sealed class PayFactsTests
{
    private static readonly DateTime RecordedAt = new(2026, 10, 29, 9, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void From_SalaryChanged_IsASalaryFactForTheEventPeriod()
    {
        PayEvent payEvent = Event(EventTypes.SalaryChanged, new DateTime(2026, 11, 1), null, new SalaryChangedPayload
        {
            salaryId = 7,
            monthlyAmount = new AvroDecimal(41200.00m),
            currency = "SEK",
            reason = "Raise",
            payComponent = "Allowance",
        });

        PayFact? fact = PayFacts.From(payEvent);

        SalaryFact salary = Assert.IsType<SalaryFact>(fact);
        Assert.Equal(("Allowance", 7, 41200.00m, "SEK", new DateOnly(2026, 11, 1), null, false),
            (salary.PayComponent, salary.SalaryId, salary.MonthlyAmount, salary.Currency, salary.ValidFrom, salary.ValidTo, salary.IsCorrection));
        Assert.Equal(new DateTimeOffset(RecordedAt), salary.RecordedAt);
        Assert.Equal(payEvent.eventId, salary.EventId);
    }

    [Fact]
    public void From_SalaryCorrected_IsACorrection()
    {
        PayEvent payEvent = Event(EventTypes.SalaryCorrected, new DateTime(2026, 5, 1), new DateTime(2026, 9, 1), new SalaryCorrectedPayload
        {
            salaryId = 7,
            monthlyAmount = new AvroDecimal(41500.00m),
            currency = "SEK",
            changedFields = ["MonthlyAmount"],
            payComponent = "Base",
        });

        SalaryFact salary = Assert.IsType<SalaryFact>(PayFacts.From(payEvent));

        Assert.True(salary.IsCorrection);
        Assert.Equal(new DateOnly(2026, 9, 1), salary.ValidTo);
    }

    [Fact]
    public void From_HireWithInitialSalary_IsASalaryFactFromTheHireDate()
    {
        PayEvent payEvent = Event(EventTypes.EmployeeHired, new DateTime(2026, 10, 15), null, new EmployeeHiredPayload
        {
            hireDate = new DateTime(2026, 10, 15),
            gender = "F",
            initialSalary = new InitialSalary { salaryId = 9, monthlyAmount = new AvroDecimal(38000.00m), currency = "SEK", payComponent = "Base" },
        });

        SalaryFact salary = Assert.IsType<SalaryFact>(PayFacts.From(payEvent));

        Assert.Equal((9, new DateOnly(2026, 10, 15), false), (salary.SalaryId, salary.ValidFrom, salary.IsCorrection));
    }

    [Fact]
    public void From_Termination_EndsPayTheDayAfterTheLastDay()
    {
        PayEvent payEvent = Event(EventTypes.EmployeeTerminated, new DateTime(2026, 10, 31), null,
            new EmployeeTerminatedPayload { terminationDate = new DateTime(2026, 10, 31) });

        TerminationFact termination = Assert.IsType<TerminationFact>(PayFacts.From(payEvent));

        Assert.Equal(new DateOnly(2026, 11, 1), termination.PayEndsOn);
    }

    [Fact]
    public void From_EventsWithoutPay_AreNull()
    {
        PayEvent snapshotHire = Event(EventTypes.EmployeeHired, new DateTime(2024, 3, 4), null,
            new EmployeeHiredPayload { hireDate = new DateTime(2024, 3, 4), gender = "M" });
        PayEvent position = Event(EventTypes.PositionChanged, new DateTime(2026, 11, 1), null,
            new PositionChangedPayload { positionId = 3, title = "Engineer", level = 2 });

        Assert.Null(PayFacts.From(snapshotHire));
        Assert.Null(PayFacts.From(position));
    }

    [Fact]
    public void From_PayloadOfAnotherEventType_Throws()
    {
        PayEvent payEvent = Event(EventTypes.SalaryChanged, new DateTime(2026, 11, 1), null,
            new EmployeeTerminatedPayload { terminationDate = new DateTime(2026, 10, 31) });

        _ = Assert.Throws<PayEventException>(() => PayFacts.From(payEvent));
    }

    private static PayEvent Event(string eventType, DateTime effectiveFrom, DateTime? effectiveTo, object payload) => new()
    {
        eventId = Guid.NewGuid(),
        eventType = eventType,
        schemaVersion = PayEventContract.SchemaVersion,
        tenantId = 1,
        employeeId = 1001,
        effectiveFrom = DateTime.SpecifyKind(effectiveFrom, DateTimeKind.Utc),
        effectiveTo = effectiveTo is { } to ? DateTime.SpecifyKind(to, DateTimeKind.Utc) : null,
        recordedAt = RecordedAt,
        isRetroactive = false,
        source = new EventSource { tables = ["dbo.Salary"], commitLsn = "0000002a:00000100:0003", snapshot = false },
        payload = payload,
    };
}
