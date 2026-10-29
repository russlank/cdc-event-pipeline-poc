namespace CdcPay.Translation.Raw;

public enum ChangeOperation
{
    Create,
    Update,
    Delete,

    /// <summary>Snapshot read (initial or incremental).</summary>
    Read,
}

/// <summary>One Debezium change event of a source table, parsed.</summary>
public sealed record RawChange(
    RawMessage Message,
    string Database,
    string Schema,
    string Table,
    ChangeOperation Operation,
    SourceRow? Before,
    SourceRow? After,
    long CommitTimestampMs,
    string CommitLsn,
    string? ChangeLsn,
    long? EventSerialNo,
    bool Snapshot,
    string? TransactionId,
    long? TotalOrder)
{
    /// <summary>"HrCore.dbo.Salary": the name used in transaction END event counts.</summary>
    public string DataCollection => $"{Database}.{Schema}.{Table}";

    /// <summary>
    /// Where the change is in the source log. A change Debezium sends again after a task restart
    /// has the same source position but a new <see cref="TotalOrder"/>.
    /// </summary>
    public string SourcePosition => $"{DataCollection}@{ChangeLsn ?? CommitLsn}/{EventSerialNo}";

    /// <summary>"dbo.Salary".</summary>
    public string QualifiedTable => $"{Schema}.{Table}";

    /// <summary>The row after the change, or before it for a delete.</summary>
    public SourceRow Row => After ?? Before ?? throw new InvalidOperationException($"Change at {Message.Position} has no row image.");
}

public enum TransactionStatus
{
    Begin,
    End,
}

/// <summary>A Debezium transaction boundary from the transaction topic.</summary>
public sealed record TransactionMarker(
    RawMessage Message,
    TransactionStatus Status,
    string TransactionId,
    long? EventCount,
    IReadOnlyDictionary<string, long> EventCountsByDataCollection,
    long TimestampMs);
