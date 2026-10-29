using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CdcPay.Common.Hosting;

/// <summary>Wiring shared by every host: identity, telemetry, health checks, and the operator endpoints.</summary>
public static class ServiceDefaultsExtensions
{
    /// <summary>Health checks tagged with this value run on <c>/health/ready</c>.</summary>
    public const string ReadyTag = "ready";

    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";
    public const string IdentityPath = "/system/component-identity";

    /// <summary>Every meter and activity source of the solution is named with this prefix.</summary>
    public const string TelemetryPrefix = "CdcPay.";

    /// <summary>Setting that turns on the OTLP export of metrics and traces (standard OpenTelemetry variable).</summary>
    public const string OtlpEndpointSetting = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>
    /// Registers the host's <see cref="ComponentIdentity"/>, OpenTelemetry metrics and traces, and
    /// health checks. Telemetry is exported over OTLP only when <see cref="OtlpEndpointSetting"/> is set.
    /// </summary>
    public static IHealthChecksBuilder AddServiceDefaults(this IHostApplicationBuilder builder, Assembly hostAssembly)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(hostAssembly);

        var identity = ComponentIdentity.FromAssembly(hostAssembly);
        builder.Services.AddSingleton(identity);

        bool export = !string.IsNullOrWhiteSpace(builder.Configuration[OtlpEndpointSetting]);
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(identity.Component, serviceVersion: identity.Version))
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(TelemetryPrefix + "*").AddAspNetCoreInstrumentation().AddRuntimeInstrumentation();
                if (export)
                {
                    metrics.AddOtlpExporter();
                }
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(TelemetryPrefix + "*")
                    .AddAspNetCoreInstrumentation(options => options.Filter = context => !IsOperatorPath(context.Request.Path));
                if (export)
                {
                    tracing.AddOtlpExporter();
                }
            });

        return builder.Services.AddHealthChecks();
    }

    /// <summary>
    /// Probes and the identity endpoint: polled by Docker and operators, so they are kept out of
    /// traces (and out of request logs, which the hosts log at Warning only).
    /// </summary>
    public static bool IsOperatorPath(PathString path) =>
        path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments(IdentityPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maps <c>/health/live</c> (no dependency checks), <c>/health/ready</c> (checks tagged
    /// <see cref="ReadyTag"/>), and <c>/system/component-identity</c>, and logs the identity line.
    /// </summary>
    public static IEndpointRouteBuilder MapServiceEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        ComponentIdentity identity = app.Services.GetRequiredService<ComponentIdentity>();
        ServiceLogs.ComponentStarting(app.Logger, identity, identity.Version, identity.Commit, identity.Branch);

        app.MapHealthChecks(LivePath, new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks(ReadyPath, new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) });
        app.MapGet(IdentityPath, () => TypedResults.Ok(identity)).WithName("ComponentIdentity");
        return app;
    }
}
