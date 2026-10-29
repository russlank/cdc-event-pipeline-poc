using Npgsql;
using NpgsqlTypes;

namespace CdcPay.PayHistory.Storage;

/// <summary>As-of queries over the read model.</summary>
public interface IPayHistoryReader
{
    /// <summary>The read model is reachable and its schema is applied.</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);

    /// <summary>The projector has applied at least one event about the employee.</summary>
    Task<bool> EmployeeExistsAsync(int tenantId, int employeeId, CancellationToken cancellationToken);

    /// <summary>
    /// The rows in effect on <paramref name="validAt"/> (one per pay component), as known at
    /// <paramref name="knownAt"/>, or as currently known when it is null.
    /// </summary>
    Task<IReadOnlyList<PayHistoryRow>> SalaryAsync(
        int tenantId, int employeeId, DateOnly validAt, DateTimeOffset? knownAt, CancellationToken cancellationToken);

    /// <summary>Every version, ordered by valid_from, recorded_from, and pay component.</summary>
    Task<IReadOnlyList<PayHistoryRow>> HistoryAsync(int tenantId, int employeeId, CancellationToken cancellationToken);
}

public sealed class PayHistoryReader(NpgsqlDataSource dataSource) : IPayHistoryReader
{
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(
            "SELECT to_regclass('pay_history') IS NOT NULL AND to_regclass('processed_event') IS NOT NULL");
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    public async Task<bool> EmployeeExistsAsync(int tenantId, int employeeId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM processed_event WHERE tenant_id = $1 AND employee_id = $2)");
        command.Parameters.Add(new NpgsqlParameter { Value = tenantId });
        command.Parameters.Add(new NpgsqlParameter { Value = employeeId });
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    public async Task<IReadOnlyList<PayHistoryRow>> SalaryAsync(
        int tenantId, int employeeId, DateOnly validAt, DateTimeOffset? knownAt, CancellationToken cancellationToken)
    {
        string recorded = knownAt is null
            ? "recorded_to IS NULL"
            : "recorded_from <= $4 AND (recorded_to IS NULL OR $4 < recorded_to)";
        await using NpgsqlCommand command = dataSource.CreateCommand(
            $"""
            SELECT {PayHistoryStore.RowColumns} FROM pay_history
            WHERE tenant_id = $1 AND employee_id = $2
              AND valid_from <= $3 AND (valid_to IS NULL OR $3 < valid_to)
              AND {recorded}
            ORDER BY pay_component
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = tenantId });
        command.Parameters.Add(new NpgsqlParameter { Value = employeeId });
        command.Parameters.Add(new NpgsqlParameter { Value = validAt, NpgsqlDbType = NpgsqlDbType.Date });
        if (knownAt is { } known)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = known.ToUniversalTime(), NpgsqlDbType = NpgsqlDbType.TimestampTz });
        }

        return await PayHistoryRows.ReadAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<PayHistoryRow>> HistoryAsync(int tenantId, int employeeId, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = dataSource.CreateCommand(
            $"""
            SELECT {PayHistoryStore.RowColumns} FROM pay_history
            WHERE tenant_id = $1 AND employee_id = $2
            ORDER BY valid_from, recorded_from, pay_component, pay_history_id
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = tenantId });
        command.Parameters.Add(new NpgsqlParameter { Value = employeeId });
        return await PayHistoryRows.ReadAsync(command, cancellationToken);
    }
}

/// <summary>Reads rows selected with <see cref="PayHistoryStore.RowColumns"/>.</summary>
internal static class PayHistoryRows
{
    public static async Task<IReadOnlyList<PayHistoryRow>> ReadAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        List<PayHistoryRow> rows = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PayHistoryRow(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetDecimal(5),
                reader.GetString(6),
                reader.GetFieldValue<DateOnly>(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateOnly>(8),
                reader.GetFieldValue<DateTimeOffset>(9),
                reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
                reader.GetGuid(11)));
        }

        return rows;
    }
}
