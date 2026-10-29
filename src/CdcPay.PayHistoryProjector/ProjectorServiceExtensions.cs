using CdcPay.Common.Hosting;
using CdcPay.PayHistory.Storage;
using Confluent.SchemaRegistry;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CdcPay.PayHistoryProjector;

/// <summary>Composition of the projector host.</summary>
public static class ProjectorServiceExtensions
{
    /// <summary>Connection string name of the read model (<c>ConnectionStrings:ReadModel</c>).</summary>
    public const string ReadModelConnection = "ReadModel";

    public static IHostApplicationBuilder AddPayHistoryProjector(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _ = builder.Services.AddOptions<ProjectorOptions>()
            .Bind(builder.Configuration.GetSection(ProjectorOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        string connectionString = builder.Configuration.GetConnectionString(ReadModelConnection)
            ?? throw new InvalidOperationException($"connection string {ReadModelConnection} is not configured");
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        builder.Services.AddSingleton<PayHistoryStore>();

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ProjectorStatus>();
        builder.Services.AddSingleton<ISchemaRegistryClient>(services =>
            new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = services.GetRequiredService<IOptions<ProjectorOptions>>().Value.SchemaRegistryUrl }));
        builder.Services.AddHostedService<ProjectorWorker>();

        builder.AddServiceDefaults(typeof(ProjectorWorker).Assembly)
            .AddCheck<ProjectorReadinessCheck>("projector", tags: [ServiceDefaultsExtensions.ReadyTag]);
        return builder;
    }
}
