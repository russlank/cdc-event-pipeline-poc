using System.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CdcPay.PayQueryApi;

/// <summary>
/// Error responses: RFC 9457 problem details with a stable <c>code</c> extension; every problem
/// also gets a <c>traceId</c> (see <see cref="AddTraceId"/>).
/// </summary>
public static class Problems
{
    public const string ValidAtRequired = "validAt-required";
    public const string ValidAtInvalid = "validAt-invalid";
    public const string KnownAtInvalid = "knownAt-invalid";
    public const string EmployeeNotFound = "employee-not-found";

    public static ProblemHttpResult BadRequest(string code, string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status400BadRequest, title: "Invalid query", extensions: Code(code));

    public static ProblemHttpResult UnknownEmployee(int tenantId, int employeeId) =>
        TypedResults.Problem(
            $"No pay events are known for employee {employeeId} of tenant {tenantId}.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Unknown employee",
            extensions: Code(EmployeeNotFound));

    /// <summary>Adds <c>traceId</c> to every problem the host writes, including framework ones.</summary>
    public static void AddTraceId(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ProblemDetails.Extensions["traceId"] = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
    }

    private static Dictionary<string, object?> Code(string code) => new(StringComparer.Ordinal) { ["code"] = code };
}
