using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CdcPay.PayQueryApi;

namespace CdcPay.UnitTests.PayQueryApi;

public sealed class SalaryEndpointsTests(QueryApiFactory factory) : IClassFixture<QueryApiFactory>
{
    private static readonly DateTimeOffset Snapshot = QueryApiFactory.Snapshot;
    private static readonly DateTimeOffset Raise = QueryApiFactory.Raise;

    [Fact]
    public async Task GetSalary_ValidAt_ReturnsTheAmountAsCurrentlyKnown()
    {
        SalaryResponse? response = await GetAsync<SalaryResponse>("/tenants/1/employees/1003/salary?validAt=2026-08-01");

        PayVersion version = Assert.Single(response!.Components);
        Assert.Equal((42000m, "Base", new DateOnly(2026, 7, 1)), (version.MonthlyAmount, version.PayComponent, version.ValidFrom));
        Assert.Null(response.KnownAt);
    }

    [Fact]
    public async Task GetSalary_KnownAtBeforeTheRaise_ReturnsTheOldAmount()
    {
        SalaryResponse? response = await GetAsync<SalaryResponse>("/tenants/1/employees/1003/salary?validAt=2026-08-01&knownAt=2026-10-29T08:30:00Z");

        Assert.Equal(40000m, Assert.Single(response!.Components).MonthlyAmount);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 8, 30, 0, TimeSpan.Zero), response.KnownAt);
    }

    [Fact]
    public async Task GetSalary_KnownAtBareDate_MeansTheEndOfThatDay()
    {
        SalaryResponse? response = await GetAsync<SalaryResponse>("/tenants/1/employees/1003/salary?validAt=2026-08-01&knownAt=2026-10-29");

        Assert.Equal(42000m, Assert.Single(response!.Components).MonthlyAmount);
    }

    [Fact]
    public async Task GetSalary_KnownEmployeeWithoutPayThatDay_ReturnsNoComponents()
    {
        SalaryResponse? response = await GetAsync<SalaryResponse>("/tenants/1/employees/1050/salary?validAt=2026-08-01");

        Assert.Empty(response!.Components);
    }

    [Fact]
    public async Task GetSalaryHistory_ReturnsEveryVersionInOrder()
    {
        SalaryHistoryResponse? response = await GetAsync<SalaryHistoryResponse>("/tenants/1/employees/1003/salary-history");

        Assert.Equal(
            [(new DateOnly(2025, 1, 1), Snapshot), (new DateOnly(2025, 1, 1), Raise), (new DateOnly(2026, 7, 1), Raise)],
            response!.Versions.Select(version => (version.ValidFrom, version.RecordedFrom)));
        Assert.Equal(Raise, response.Versions[0].RecordedTo);
    }

    [Theory]
    [InlineData("/tenants/1/employees/1003/salary", HttpStatusCode.BadRequest, Problems.ValidAtRequired)]
    [InlineData("/tenants/1/employees/1003/salary?validAt=01-08-2026", HttpStatusCode.BadRequest, Problems.ValidAtInvalid)]
    [InlineData("/tenants/1/employees/1003/salary?validAt=2026-08-01&knownAt=soon", HttpStatusCode.BadRequest, Problems.KnownAtInvalid)]
    [InlineData("/tenants/1/employees/9999/salary?validAt=2026-08-01", HttpStatusCode.NotFound, Problems.EmployeeNotFound)]
    [InlineData("/tenants/1/employees/9999/salary-history", HttpStatusCode.NotFound, Problems.EmployeeNotFound)]
    public async Task Queries_InvalidInputOrUnknownEmployee_ReturnProblemDetailsWithCodeAndTraceId(string path, HttpStatusCode status, string code)
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal((int)status, problem.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(problem.RootElement.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task UnknownRoute_ReturnsProblemDetailsWithTraceId()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri("/tenants/one/employees/1003/salary", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(problem.RootElement.TryGetProperty("traceId", out _));
    }

    private async Task<T?> GetAsync<T>(string path)
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(TestContext.Current.CancellationToken);
    }
}
