using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CdcPay.PayHistoryProjector;

/// <summary>Shared state between the consumer loop and the readiness check.</summary>
public sealed class ProjectorStatus
{
    public int AssignedPartitions
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }

    public bool SchemaApplied
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }
}

/// <summary>Ready once the read-model schema is applied and the consumer owns partitions.</summary>
public sealed class ProjectorReadinessCheck(ProjectorStatus status) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult((status.SchemaApplied, status.AssignedPartitions) switch
        {
            (false, _) => HealthCheckResult.Unhealthy("read-model schema not applied yet"),
            (true, 0) => HealthCheckResult.Unhealthy("no partitions assigned yet"),
            (true, int partitions) => HealthCheckResult.Healthy($"{partitions} partitions assigned"),
        });
}
