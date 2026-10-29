using CdcPay.Contracts.Events;
using Npgsql;
using NpgsqlTypes;

namespace CdcPay.PayHistory.Storage;

/// <summary>Result of projecting one business event.</summary>
public enum ProjectionResult
{
    /// <summary>The event changed the pay history.</summary>
    Applied,

    /// <summary>The event was recorded; the pay history already said the same, or the event is not about pay.</summary>
    NoChange,

    /// <summary>The event id was processed before; nothing was done.</summary>
    Duplicate,
}

/// <summary>
/// Writes business events into the read model. One database transaction per event: the event id
/// goes into <c>processed_event</c> (a conflict means it was seen) and the merge plan is executed,
/// or nothing is.
/// </summary>
public sealed class PayHistoryStore(NpgsqlDataSource dataSource)
{
    internal const string RowColumns =
        "pay_history_id, tenant_id, employee_id, pay_component, salary_id, monthly_amount, currency, " +
        "valid_from, valid_to, recorded_from, recorded_to, event_id";

    public async Task<ProjectionResult> ProjectAsync(PayEvent payEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payEvent);
        PayFact? fact = PayFacts.From(payEvent);

        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (!await MarkProcessedAsync(connection, transaction, payEvent, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ProjectionResult.Duplicate;
        }

        MergePlan plan = fact is null
            ? MergePlan.None
            : PayHistoryMerge.Apply(await CurrentRowsAsync(connection, transaction, fact, cancellationToken), fact);
        if (!plan.IsEmpty)
        {
            await ExecuteAsync(connection, transaction, plan, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return plan.IsEmpty ? ProjectionResult.NoChange : ProjectionResult.Applied;
    }

    private static async Task<bool> MarkProcessedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, PayEvent payEvent, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            """
            INSERT INTO processed_event (event_id, event_type, tenant_id, employee_id, recorded_at)
            VALUES (@event_id, @event_type, @tenant_id, @employee_id, @recorded_at)
            ON CONFLICT (event_id) DO NOTHING
            """,
            connection,
            transaction);
        _ = command.Parameters.AddWithValue("event_id", payEvent.eventId);
        _ = command.Parameters.AddWithValue("event_type", payEvent.eventType);
        _ = command.Parameters.AddWithValue("tenant_id", payEvent.tenantId);
        _ = command.Parameters.AddWithValue("employee_id", payEvent.employeeId);
        _ = command.Parameters.AddWithValue("recorded_at", NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(payEvent.recordedAt, DateTimeKind.Utc));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>The employee's current rows, locked until the transaction ends.</summary>
    private static async Task<IReadOnlyList<PayHistoryRow>> CurrentRowsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, PayFact fact, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            $"""
            SELECT {RowColumns} FROM pay_history
            WHERE tenant_id = @tenant_id AND employee_id = @employee_id AND recorded_to IS NULL
            FOR UPDATE
            """,
            connection,
            transaction);
        _ = command.Parameters.AddWithValue("tenant_id", fact.TenantId);
        _ = command.Parameters.AddWithValue("employee_id", fact.EmployeeId);
        return await PayHistoryRows.ReadAsync(command, cancellationToken);
    }

    /// <summary>Closes and deletes before inserting, so the no-overlap constraint holds after every statement.</summary>
    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, MergePlan plan, CancellationToken cancellationToken)
    {
        await using NpgsqlBatch batch = new(connection, transaction);

        if (plan.Close.Count > 0)
        {
            NpgsqlBatchCommand close = new("UPDATE pay_history SET recorded_to = $1 WHERE pay_history_id = ANY($2) AND recorded_to IS NULL");
            close.Parameters.Add(new NpgsqlParameter { Value = plan.At, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            close.Parameters.Add(new NpgsqlParameter { Value = plan.Close.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
            batch.BatchCommands.Add(close);
        }

        if (plan.Delete.Count > 0)
        {
            NpgsqlBatchCommand delete = new("DELETE FROM pay_history WHERE pay_history_id = ANY($1)");
            delete.Parameters.Add(new NpgsqlParameter { Value = plan.Delete.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
            batch.BatchCommands.Add(delete);
        }

        foreach (PayHistoryRow row in plan.Insert)
        {
            NpgsqlBatchCommand insert = new(
                """
                INSERT INTO pay_history (tenant_id, employee_id, pay_component, salary_id, monthly_amount, currency,
                                         valid_from, valid_to, recorded_from, recorded_to, event_id)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, NULL, $10)
                """);
            insert.Parameters.Add(new NpgsqlParameter { Value = row.TenantId });
            insert.Parameters.Add(new NpgsqlParameter { Value = row.EmployeeId });
            insert.Parameters.Add(new NpgsqlParameter { Value = row.PayComponent });
            insert.Parameters.Add(new NpgsqlParameter { Value = row.SalaryId });
            insert.Parameters.Add(new NpgsqlParameter { Value = row.MonthlyAmount, NpgsqlDbType = NpgsqlDbType.Numeric });
            insert.Parameters.Add(new NpgsqlParameter { Value = row.Currency });
            insert.Parameters.Add(new NpgsqlParameter { Value = row.ValidFrom, NpgsqlDbType = NpgsqlDbType.Date });
            insert.Parameters.Add(new NpgsqlParameter { Value = row.ValidTo is { } to ? to : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
            insert.Parameters.Add(new NpgsqlParameter { Value = row.RecordedFrom, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            insert.Parameters.Add(new NpgsqlParameter { Value = row.EventId });
            batch.BatchCommands.Add(insert);
        }

        _ = await batch.ExecuteNonQueryAsync(cancellationToken);
    }
}
