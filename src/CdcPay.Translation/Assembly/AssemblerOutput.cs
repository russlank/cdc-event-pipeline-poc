using CdcPay.Translation.Raw;

namespace CdcPay.Translation.Assembly;

/// <summary>A unit of work released by the <see cref="TransactionAssembler"/>, in commit order.</summary>
public abstract record AssemblerOutput
{
    /// <summary>Every consumed message this output accounts for; done once the output is handled.</summary>
    public abstract IReadOnlyList<KafkaPosition> Positions { get; }
}

/// <summary>A complete source transaction: every row the END event announced has arrived.</summary>
public sealed record CompletedTransaction(string TransactionId, IReadOnlyList<RawChange> Rows, TransactionMarker End) : AssemblerOutput
{
    public override IReadOnlyList<KafkaPosition> Positions =>
        [.. Rows.Select(row => row.Message.Position), End.Message.Position];
}

/// <summary>A snapshot row (initial or incremental): no transaction metadata, handled on its own.</summary>
public sealed record SnapshotRow(RawChange Row) : AssemblerOutput
{
    public override IReadOnlyList<KafkaPosition> Positions => [Row.Message.Position];
}

/// <summary>
/// Rows that cannot form a complete transaction in time: the END never arrived, rows are
/// missing, or rows arrived after their transaction was closed. They go to the dead-letter topic.
/// </summary>
public sealed record AbandonedTransaction(string TransactionId, IReadOnlyList<RawChange> Rows, TransactionMarker? End, string Detail) : AssemblerOutput
{
    public override IReadOnlyList<KafkaPosition> Positions =>
        End is null
            ? [.. Rows.Select(row => row.Message.Position)]
            : [.. Rows.Select(row => row.Message.Position), End.Message.Position];
}

/// <summary>A message with nothing left to do (a BEGIN marker, or an END seen again after a restart).</summary>
public sealed record Discarded(KafkaPosition Position) : AssemblerOutput
{
    public override IReadOnlyList<KafkaPosition> Positions => [Position];
}
