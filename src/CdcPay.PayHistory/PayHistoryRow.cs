namespace CdcPay.PayHistory;

/// <summary>
/// One version of one pay component's amount: it applies during the valid period
/// [<see cref="ValidFrom"/>, <see cref="ValidTo"/>) and was believed during the recorded period
/// [<see cref="RecordedFrom"/>, <see cref="RecordedTo"/>). A null end is open.
/// <see cref="PayHistoryId"/> is 0 for a row that is not stored yet; <see cref="SalaryId"/> is the
/// source <c>Salary</c> row the amount came from (a correction retracts by it); <see cref="EventId"/>
/// is the business event that stated the amount.
/// </summary>
public sealed record PayHistoryRow(
    long PayHistoryId,
    int TenantId,
    int EmployeeId,
    string PayComponent,
    int SalaryId,
    decimal MonthlyAmount,
    string Currency,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    DateTimeOffset RecordedFrom,
    DateTimeOffset? RecordedTo,
    Guid EventId)
{
    /// <summary>True when the valid period shares at least one day with [from, to).</summary>
    public bool Overlaps(DateOnly from, DateOnly? to) =>
        ValidFrom < (to ?? DateOnly.MaxValue) && from < (ValidTo ?? DateOnly.MaxValue);
}
