using Microsoft.Extensions.Logging;

namespace CdcPay.Common.Hosting;

/// <summary>Log messages of the shared host wiring. Event ids 1000-1099.</summary>
internal static partial class ServiceLogs
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
        Message = "Starting {Identity}; version {Version}, commit {Commit}, branch {Branch}")]
    public static partial void ComponentStarting(
        ILogger logger, ComponentIdentity identity, string version, string? commit, string? branch);
}
