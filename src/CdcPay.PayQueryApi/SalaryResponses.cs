using CdcPay.PayHistory;

namespace CdcPay.PayQueryApi;

/// <summary>One recorded version of a pay component's amount.</summary>
public sealed record PayVersion(
    string PayComponent,
    decimal MonthlyAmount,
    string Currency,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    DateTimeOffset RecordedFrom,
    DateTimeOffset? RecordedTo,
    int SalaryId,
    Guid EventId)
{
    public static PayVersion From(PayHistoryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new(row.PayComponent, row.MonthlyAmount, row.Currency, row.ValidFrom, row.ValidTo,
            row.RecordedFrom, row.RecordedTo, row.SalaryId, row.EventId);
    }
}

/// <summary>
/// The pay in effect on <see cref="ValidAt"/>, one entry per pay component, as known at
/// <see cref="KnownAt"/> (null: as currently known). Empty when no pay applies that day.
/// </summary>
public sealed record SalaryResponse(
    int TenantId,
    int EmployeeId,
    DateOnly ValidAt,
    DateTimeOffset? KnownAt,
    IReadOnlyList<PayVersion> Components);

/// <summary>Every recorded version, ordered by valid from, recorded from, and pay component.</summary>
public sealed record SalaryHistoryResponse(
    int TenantId,
    int EmployeeId,
    IReadOnlyList<PayVersion> Versions);
