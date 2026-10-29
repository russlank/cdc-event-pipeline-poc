namespace CdcPay.Contracts;

/// <summary>Values of <c>PayEvent.eventType</c>; each names one payload branch.</summary>
public static class EventTypes
{
    public const string EmployeeHired = nameof(EmployeeHired);
    public const string SalaryChanged = nameof(SalaryChanged);
    public const string SalaryCorrected = nameof(SalaryCorrected);
    public const string PositionChanged = nameof(PositionChanged);
    public const string EmployeeTerminated = nameof(EmployeeTerminated);
}
