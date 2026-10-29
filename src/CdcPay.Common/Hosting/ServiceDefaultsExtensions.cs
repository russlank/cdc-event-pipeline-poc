using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CdcPay.Common.Hosting;

/// <summary>Wiring shared by every host: identity, health checks, and the operator endpoints.</summary>
public static class ServiceDefaultsExtensions
{
    /// <summary>Health checks tagged with this value run on <c>/health/ready</c>.</summary>
    public const string ReadyTag = "ready";

    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";
    public const string IdentityPath = "/system/component-identity";

    /// <summary>Registers the host's <see cref="ComponentIdentity"/> and health checks.</summary>
    public static IHealthChecksBuilder AddServiceDefaults(this IHostApplicationBuilder builder, Assembly hostAssembly)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(hostAssembly);

        builder.Services.AddSingleton(ComponentIdentity.FromAssembly(hostAssembly));
        return builder.Services.AddHealthChecks();
    }

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
