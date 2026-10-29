using CdcPay.Translation.Assembly;
using CdcPay.Translation.Raw;

namespace CdcPay.UnitTests.Translation;

/// <summary>Builders for raw changes, transaction markers, and complete transactions in tests.</summary>
internal static class Changes
{
    /// <summary>Commit time used by default: 2026-10-29T10:00:00Z.</summary>
    public static readonly long CommitMs = new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static long s_offset;

    public static RawChange Create(SourceRow after, string tx = "tx-1", long order = 1, long? commitMs = null) =>
        Change(ChangeOperation.Create, null, after, tx, order, commitMs);

    public static RawChange Update(SourceRow before, SourceRow after, string tx = "tx-1", long order = 1, long? commitMs = null) =>
        Change(ChangeOperation.Update, before, after, tx, order, commitMs);

    public static RawChange Delete(SourceRow before, string tx = "tx-1", long order = 1) =>
        Change(ChangeOperation.Delete, before, null, tx, order, null);

    public static RawChange Snapshot(SourceRow row, string commitLsn = "0000002d:000010d8:008f", long? readMs = null) => new(
        Message(TableOf(row)),
        "HrCore",
        "dbo",
        TableOf(row),
        ChangeOperation.Read,
        null,
        row,
        readMs ?? CommitMs,
        commitLsn,
        null,
        true,
        null,
        null);

    public static TransactionMarker End(string tx, params RawChange[] rows) => new(
        new RawMessage(new KafkaPosition("cdc.transaction", 0, Interlocked.Increment(ref s_offset)), null, []),
        TransactionStatus.End,
        tx,
        rows.Length,
        rows.GroupBy(row => row.DataCollection).ToDictionary(group => group.Key, group => (long)group.Count()),
        CommitMs);

    public static TransactionMarker Begin(string tx) => new(
        new RawMessage(new KafkaPosition("cdc.transaction", 0, Interlocked.Increment(ref s_offset)), null, []),
        TransactionStatus.Begin,
        tx,
        null,
        new Dictionary<string, long>(),
        CommitMs);

    /// <summary>A complete transaction of the given rows, with an END that announces exactly them.</summary>
    public static CompletedTransaction Transaction(params RawChange[] rows) =>
        new(rows[0].TransactionId!, rows, End(rows[0].TransactionId!, rows));

    public static EmployeeRow Employee(int employeeId = 1001, int tenantId = 1, string gender = "F", DateOnly? hired = null, DateOnly? terminated = null) =>
        new(tenantId, employeeId, $"Employee {employeeId}", gender, hired ?? new DateOnly(2025, 8, 2), terminated);

    public static SalaryRow Salary(
        int salaryId,
        decimal amount,
        DateOnly from,
        DateOnly? to = null,
        string reason = "Raise",
        string currency = "SEK",
        int employeeId = 1001,
        int tenantId = 1) =>
        new(salaryId, tenantId, employeeId, amount, currency, from, to, reason);

    public static PositionRow Position(int positionId, string title, int level, DateOnly from, DateOnly? to = null, int employeeId = 1001, int tenantId = 1) =>
        new(positionId, tenantId, employeeId, title, level, from, to);

    public static OtherRow Other(string table) => new(table);

    private static RawChange Change(ChangeOperation operation, SourceRow? before, SourceRow? after, string tx, long order, long? commitMs)
    {
        string table = TableOf(after ?? before!);
        return new RawChange(
            Message(table),
            "HrCore",
            "dbo",
            table,
            operation,
            before,
            after,
            commitMs ?? CommitMs,
            $"lsn-{tx}",
            $"lsn-{tx}-{order}",
            false,
            tx,
            order);
    }

    private static RawMessage Message(string table) =>
        new(new KafkaPosition($"cdc.HrCore.dbo.{table}", 0, Interlocked.Increment(ref s_offset)), null, [1]);

    private static string TableOf(SourceRow row) => row switch
    {
        EmployeeRow => "Employee",
        SalaryRow => "Salary",
        PositionRow => "Position",
        OtherRow other => other.Table,
        _ => throw new ArgumentException("unknown row", nameof(row)),
    };
}
