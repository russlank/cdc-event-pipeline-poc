extern alias projector;

using System.Net;
using System.Net.Sockets;
using Confluent.SchemaRegistry;
using Npgsql;
using projector::CdcPay.PayHistoryProjector;

namespace CdcPay.UnitTests.PayHistoryProjector;

public sealed class ProjectorFailuresTests
{
    public static TheoryData<Exception, bool> Failures => new()
    {
        { new NpgsqlException("connection refused", new SocketException((int)SocketError.ConnectionRefused)), true },
        { new NpgsqlException("not transient"), false },
        { new PostgresException("conflicting key value violates exclusion constraint", "ERROR", "ERROR", PostgresErrorCodes.ExclusionViolation), false },
        { new PostgresException("terminating connection due to administrator command", "FATAL", "FATAL", PostgresErrorCodes.AdminShutdown), true },
        { new HttpRequestException("registry unreachable"), true },
        { new SchemaRegistryException("unavailable", HttpStatusCode.ServiceUnavailable, 50301), true },
        { new SchemaRegistryException("too many requests", HttpStatusCode.TooManyRequests, 42901), true },
        { new SchemaRegistryException("schema not found", HttpStatusCode.NotFound, 40403), false },
        { new InvalidDataException("unknown magic byte"), false },
    };

    [Theory]
    [MemberData(nameof(Failures), DisableDiscoveryEnumeration = true)]
    public void IsTransient_RetriesOnlyUnreachableOrOverloadedDependencies(Exception exception, bool transient) =>
        Assert.Equal(transient, ProjectorFailures.IsTransient(exception));
}
