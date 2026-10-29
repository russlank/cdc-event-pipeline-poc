using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CdcPay.EventTranslator;

/// <summary>Shared state between the consumer loop and the readiness check.</summary>
public sealed class TranslatorStatus
{
    public int AssignedPartitions
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }
}

/// <summary>Ready once the consumer owns partitions; before that the translator is not doing its job.</summary>
public sealed class TranslatorReadinessCheck(TranslatorStatus status) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(status.AssignedPartitions > 0
            ? HealthCheckResult.Healthy($"{status.AssignedPartitions} partitions assigned")
            : HealthCheckResult.Unhealthy("no partitions assigned yet"));
}
