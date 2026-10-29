using CdcPay.PayHistory;
using CdcPay.PayHistory.Storage;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CdcPay.PayQueryApi;

/// <summary>The as-of salary queries.</summary>
public static class SalaryEndpoints
{
    public static IEndpointRouteBuilder MapSalaryQueries(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder employee = app.MapGroup("/tenants/{tenantId:int}/employees/{employeeId:int}");
        _ = employee.MapGet("/salary", GetSalaryAsync).WithName("GetSalary");
        _ = employee.MapGet("/salary-history", GetSalaryHistoryAsync).WithName("GetSalaryHistory");
        return app;
    }

    /// <summary><c>validAt</c> is required; <c>knownAt</c> is optional (see <see cref="AsOf"/>).</summary>
    public static async Task<Results<Ok<SalaryResponse>, ProblemHttpResult>> GetSalaryAsync(
        int tenantId, int employeeId, string? validAt, string? knownAt, IPayHistoryReader reader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (string.IsNullOrEmpty(validAt))
        {
            return Problems.BadRequest(Problems.ValidAtRequired, "Query parameter validAt (yyyy-MM-dd) is required.");
        }

        if (!AsOf.TryParseValidAt(validAt, out DateOnly validDate))
        {
            return Problems.BadRequest(Problems.ValidAtInvalid, "validAt must be a date, yyyy-MM-dd.");
        }

        DateTimeOffset? knownInstant = null;
        if (knownAt is not null)
        {
            if (!AsOf.TryParseKnownAt(knownAt, out DateTimeOffset parsed))
            {
                return Problems.BadRequest(Problems.KnownAtInvalid, "knownAt must be an ISO 8601 date-time or a date, yyyy-MM-dd.");
            }

            knownInstant = parsed;
        }

        if (!await reader.EmployeeExistsAsync(tenantId, employeeId, cancellationToken))
        {
            return Problems.UnknownEmployee(tenantId, employeeId);
        }

        IReadOnlyList<PayHistoryRow> rows = await reader.SalaryAsync(tenantId, employeeId, validDate, knownInstant, cancellationToken);
        return TypedResults.Ok(new SalaryResponse(tenantId, employeeId, validDate, knownInstant, [.. rows.Select(PayVersion.From)]));
    }

    public static async Task<Results<Ok<SalaryHistoryResponse>, ProblemHttpResult>> GetSalaryHistoryAsync(
        int tenantId, int employeeId, IPayHistoryReader reader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (!await reader.EmployeeExistsAsync(tenantId, employeeId, cancellationToken))
        {
            return Problems.UnknownEmployee(tenantId, employeeId);
        }

        IReadOnlyList<PayHistoryRow> rows = await reader.HistoryAsync(tenantId, employeeId, cancellationToken);
        return TypedResults.Ok(new SalaryHistoryResponse(tenantId, employeeId, [.. rows.Select(PayVersion.From)]));
    }
}
