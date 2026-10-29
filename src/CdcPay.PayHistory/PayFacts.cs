using Avro;
using CdcPay.Contracts;
using CdcPay.Contracts.Events;

namespace CdcPay.PayHistory;

/// <summary>What a business event says about pay, recorded at <see cref="RecordedAt"/>.</summary>
public abstract record PayFact(int TenantId, int EmployeeId, DateTimeOffset RecordedAt, Guid EventId);

/// <summary>
/// The amount of one pay component for a valid period: from a hire, a raise, a promotion, a
/// snapshot row, or a correction. A correction first retracts what its salary row said before.
/// </summary>
public sealed record SalaryFact(
    int TenantId,
    int EmployeeId,
    DateTimeOffset RecordedAt,
    Guid EventId,
    string PayComponent,
    int SalaryId,
    decimal MonthlyAmount,
    string Currency,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    bool IsCorrection) : PayFact(TenantId, EmployeeId, RecordedAt, EventId);

/// <summary>
/// The employee leaves: no pay component applies from <see cref="PayEndsOn"/>, the day after the
/// last day of employment.
/// </summary>
public sealed record TerminationFact(
    int TenantId,
    int EmployeeId,
    DateTimeOffset RecordedAt,
    Guid EventId,
    DateOnly PayEndsOn) : PayFact(TenantId, EmployeeId, RecordedAt, EventId);

/// <summary>Maps business events to pay facts.</summary>
public static class PayFacts
{
    /// <summary>
    /// The pay fact of an event, or null when the event says nothing about pay (a position
    /// change, a hire without an initial salary).
    /// </summary>
    /// <exception cref="PayEventException">The payload does not match the event type.</exception>
    public static PayFact? From(PayEvent payEvent)
    {
        ArgumentNullException.ThrowIfNull(payEvent);

        var recordedAt = new DateTimeOffset(DateTime.SpecifyKind(payEvent.recordedAt, DateTimeKind.Utc));
        DateOnly from = Date(payEvent.effectiveFrom);
        DateOnly? to = payEvent.effectiveTo is { } end ? Date(end) : null;

        return (payEvent.eventType, payEvent.payload) switch
        {
            (EventTypes.EmployeeHired, EmployeeHiredPayload { initialSalary: { } salary }) =>
                new SalaryFact(payEvent.tenantId, payEvent.employeeId, recordedAt, payEvent.eventId,
                    salary.payComponent, salary.salaryId, Amount(salary.monthlyAmount), salary.currency, from, to, IsCorrection: false),
            (EventTypes.EmployeeHired, EmployeeHiredPayload) => null,
            (EventTypes.SalaryChanged, SalaryChangedPayload changed) =>
                new SalaryFact(payEvent.tenantId, payEvent.employeeId, recordedAt, payEvent.eventId,
                    changed.payComponent, changed.salaryId, Amount(changed.monthlyAmount), changed.currency, from, to, IsCorrection: false),
            (EventTypes.SalaryCorrected, SalaryCorrectedPayload corrected) =>
                new SalaryFact(payEvent.tenantId, payEvent.employeeId, recordedAt, payEvent.eventId,
                    corrected.payComponent, corrected.salaryId, Amount(corrected.monthlyAmount), corrected.currency, from, to, IsCorrection: true),
            (EventTypes.EmployeeTerminated, EmployeeTerminatedPayload terminated) =>
                new TerminationFact(payEvent.tenantId, payEvent.employeeId, recordedAt, payEvent.eventId, Date(terminated.terminationDate).AddDays(1)),
            (EventTypes.PositionChanged, PositionChangedPayload) => null,
            _ => throw new PayEventException($"event type {DiagnosticValues.Code(payEvent.eventType)} does not match payload {payEvent.payload?.GetType().Name ?? "null"}"),
        };
    }

    private static DateOnly Date(DateTime avroDate) => DateOnly.FromDateTime(avroDate);

    private static decimal Amount(AvroDecimal amount) => (decimal)amount;
}

/// <summary>A business event that the read model cannot apply.</summary>
public sealed class PayEventException : Exception
{
    public PayEventException()
    {
    }

    public PayEventException(string message)
        : base(message)
    {
    }

    public PayEventException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
