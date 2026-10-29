using Avro;
using CdcPay.Contracts;
using CdcPay.Contracts.Events;

namespace CdcPay.IntegrationTests;

/// <summary>
/// A business event stream for two employees. 1:1003: Base 40000 from 2025-01 (snapshot), a
/// retroactive raise to 42000 from 2026-07, its correction to 42500, a position change.
/// 1:1101: hired 2026-10-15 at 38000, terminated 2026-12-31.
/// </summary>
internal static class FixtureStream
{
    public static readonly DateTime Snapshot = Utc(2026, 10, 29, 8, 0);
    public static readonly DateTime Hire = Utc(2026, 10, 29, 8, 30);
    public static readonly DateTime RetroactiveRaise = Utc(2026, 10, 29, 9, 0);
    public static readonly DateTime Correction = Utc(2026, 10, 29, 9, 30);
    public static readonly DateTime Termination = Utc(2026, 10, 29, 10, 0);

    public static IReadOnlyList<PayEvent> Events { get; } =
    [
        Event(EventTypes.SalaryChanged, 1003, Snapshot, Date(2025, 1, 1), snapshot: true, new SalaryChangedPayload
        {
            salaryId = 1, monthlyAmount = Amount(40000), currency = "SEK", reason = "Hire", payComponent = "Base",
        }),
        Event(EventTypes.EmployeeHired, 1101, Hire, Date(2026, 10, 15), snapshot: false, new EmployeeHiredPayload
        {
            hireDate = Date(2026, 10, 15),
            gender = "F",
            initialSalary = new InitialSalary { salaryId = 10, monthlyAmount = Amount(38000), currency = "SEK", payComponent = "Base" },
            initialPosition = new InitialPosition { positionId = 10, title = "Analyst", level = 1 },
        }),
        Event(EventTypes.SalaryChanged, 1003, RetroactiveRaise, Date(2026, 7, 1), snapshot: false, new SalaryChangedPayload
        {
            salaryId = 2, monthlyAmount = Amount(42000), currency = "SEK", reason = "Raise", previousMonthlyAmount = Amount(40000), payComponent = "Base",
        }),
        Event(EventTypes.SalaryCorrected, 1003, Correction, Date(2026, 7, 1), snapshot: false, new SalaryCorrectedPayload
        {
            salaryId = 2, monthlyAmount = Amount(42500), currency = "SEK", changedFields = ["MonthlyAmount"], previousMonthlyAmount = Amount(42000), payComponent = "Base",
        }),
        Event(EventTypes.PositionChanged, 1003, Correction, Date(2026, 7, 1), snapshot: false, new PositionChangedPayload
        {
            positionId = 3, title = "Senior Analyst", level = 3, previousTitle = "Analyst", previousLevel = 2,
        }),
        Event(EventTypes.EmployeeTerminated, 1101, Termination, Date(2026, 12, 31), snapshot: false, new EmployeeTerminatedPayload
        {
            terminationDate = Date(2026, 12, 31),
        }),
    ];

    private static PayEvent Event(string eventType, int employeeId, DateTime recordedAt, DateTime effectiveFrom, bool snapshot, object payload) => new()
    {
        eventId = Guid.NewGuid(),
        eventType = eventType,
        schemaVersion = PayEventContract.SchemaVersion,
        tenantId = 1,
        employeeId = employeeId,
        effectiveFrom = effectiveFrom,
        effectiveTo = null,
        recordedAt = recordedAt,
        isRetroactive = !snapshot && effectiveFrom < recordedAt.Date,
        source = new EventSource
        {
            tables = ["dbo.Salary"],
            commitLsn = "0000002a:00000100:0003",
            changeLsn = snapshot ? null : "0000002a:00000100:0002",
            transactionId = snapshot ? null : "1:1",
            snapshot = snapshot,
        },
        payload = payload,
    };

    private static DateTime Utc(int year, int month, int day, int hour, int minute) => new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static DateTime Date(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static AvroDecimal Amount(decimal value) => new(value + 0.00m);
}
