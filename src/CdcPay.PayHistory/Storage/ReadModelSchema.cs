using System.Reflection;
using Npgsql;

namespace CdcPay.PayHistory.Storage;

/// <summary>Applies the read-model schema (<c>db/readmodel/*.sql</c>, embedded) in file-name order.</summary>
public static class ReadModelSchema
{
    private const string ResourcePrefix = "readmodel.";

    // Serializes concurrent appliers (two projector instances, a test and a host).
    private const long AdvisoryLockKey = 0x43_44_43_50_41_59; // "CDCPAY"

    public static IReadOnlyList<string> ScriptNames { get; } =
    [
        .. typeof(ReadModelSchema).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal),
    ];

    public static async Task ApplyAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (NpgsqlCommand lockCommand = new("SELECT pg_advisory_xact_lock(@key)", connection, transaction))
        {
            _ = lockCommand.Parameters.AddWithValue("key", AdvisoryLockKey);
            _ = await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (string name in ScriptNames)
        {
            await using NpgsqlCommand command = new(Read(name), connection, transaction);
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static string Read(string resourceName)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"missing resource {resourceName}");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
