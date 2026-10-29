using CdcPay.PayHistory;
using CdcPay.PayHistory.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CdcPay.UnitTests.PayQueryApi;

/// <summary>
/// The Query API host with an in-memory read model: employee 1:1003 had Base 40000 from 2025-01,
/// believed at <see cref="Snapshot"/>; a raise to 42000 from 2026-07 was recorded at
/// <see cref="Raise"/>. Employee 1:1050 is known but has no pay rows.
/// </summary>
public sealed class QueryApiFactory : WebApplicationFactory<Program>
{
    public static readonly DateTimeOffset Snapshot = new(2026, 10, 29, 8, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset Raise = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    public QueryApiFactory()
    {
        _ = Reader.Employees.Add((1, 1003));
        _ = Reader.Employees.Add((1, 1050));
        Reader.Rows.AddRange(
        [
            Row(1, 40000, new DateOnly(2025, 1, 1), null, Snapshot, Raise),
            Row(2, 40000, new DateOnly(2025, 1, 1), new DateOnly(2026, 7, 1), Raise, null),
            Row(3, 42000, new DateOnly(2026, 7, 1), null, Raise, null),
        ]);
    }

    public FakePayHistoryReader Reader { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services => services.AddSingleton<IPayHistoryReader>(Reader));

    private static PayHistoryRow Row(long id, decimal amount, DateOnly from, DateOnly? to, DateTimeOffset recordedFrom, DateTimeOffset? recordedTo) =>
        new(id, 1, 1003, "Base", (int)id, amount, "SEK", from, to, recordedFrom, recordedTo, Guid.NewGuid());
}

/// <summary>Answers queries from a fixed list of rows with the semantics of the real reader.</summary>
public sealed class FakePayHistoryReader : IPayHistoryReader
{
    public List<PayHistoryRow> Rows { get; } = [];

    public HashSet<(int TenantId, int EmployeeId)> Employees { get; } = [];

    public bool Available { get; set; } = true;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(Available);

    public Task<bool> EmployeeExistsAsync(int tenantId, int employeeId, CancellationToken cancellationToken) =>
        Task.FromResult(Employees.Contains((tenantId, employeeId)));

    public Task<IReadOnlyList<PayHistoryRow>> SalaryAsync(
        int tenantId, int employeeId, DateOnly validAt, DateTimeOffset? knownAt, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PayHistoryRow>>(
        [
            .. Of(tenantId, employeeId).Where(row =>
                row.ValidFrom <= validAt && (row.ValidTo is null || validAt < row.ValidTo)
                && (knownAt is not { } known
                    ? row.RecordedTo is null
                    : row.RecordedFrom <= known && (row.RecordedTo is null || known < row.RecordedTo))),
        ]);

    public Task<IReadOnlyList<PayHistoryRow>> HistoryAsync(int tenantId, int employeeId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PayHistoryRow>>([.. Of(tenantId, employeeId).OrderBy(row => row.ValidFrom).ThenBy(row => row.RecordedFrom)]);

    private IEnumerable<PayHistoryRow> Of(int tenantId, int employeeId) =>
        Rows.Where(row => row.TenantId == tenantId && row.EmployeeId == employeeId);
}
