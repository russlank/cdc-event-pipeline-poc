using CdcPay.Common.Hosting;
using CdcPay.PayHistory.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace CdcPay.PayQueryApi;

/// <summary>Composition of the Query API host.</summary>
public static class QueryApiServiceExtensions
{
    /// <summary>Connection string name of the read model (<c>ConnectionStrings:ReadModel</c>).</summary>
    public const string ReadModelConnection = "ReadModel";

    public static IHostApplicationBuilder AddPayQueryApi(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _ = builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = Problems.AddTraceId);

        // Resolved lazily, so a host whose reader is replaced (tests) needs no connection string.
        builder.Services.AddSingleton(services =>
            NpgsqlDataSource.Create(services.GetRequiredService<IConfiguration>().GetConnectionString(ReadModelConnection)
                ?? throw new InvalidOperationException($"connection string {ReadModelConnection} is not configured")));
        builder.Services.AddSingleton<IPayHistoryReader, PayHistoryReader>();

        builder.AddServiceDefaults(typeof(QueryApiServiceExtensions).Assembly)
            .AddCheck<ReadModelReadinessCheck>("read-model", tags: [ServiceDefaultsExtensions.ReadyTag]);
        return builder;
    }

    public static WebApplication MapPayQueryApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        _ = app.UseExceptionHandler();
        _ = app.UseStatusCodePages();
        _ = app.MapServiceEndpoints();
        _ = app.MapSalaryQueries();
        return app;
    }
}

/// <summary>Ready once the read model is reachable and its schema is applied (by the projector).</summary>
public sealed class ReadModelReadinessCheck(IPayHistoryReader reader) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await reader.IsAvailableAsync(cancellationToken)
                ? HealthCheckResult.Healthy("read model available")
                : HealthCheckResult.Unhealthy("read-model schema not applied yet");
        }
        catch (NpgsqlException exception)
        {
            return HealthCheckResult.Unhealthy("read model unreachable", exception);
        }
    }
}
