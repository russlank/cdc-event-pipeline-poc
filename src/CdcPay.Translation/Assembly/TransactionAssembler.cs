using CdcPay.Translation.Raw;

namespace CdcPay.Translation.Assembly;

/// <summary>
/// Groups raw rows into source transactions using Debezium transaction metadata.
/// Rows of one transaction arrive on several topics in any order; a transaction is complete
/// when its END event has arrived and every per-table count it announces is met.
/// Transactions are released in the order of their END events (commit order); a later
/// complete transaction waits for an earlier incomplete one until that times out.
/// Not thread-safe: one consumer loop owns it.
/// </summary>
public sealed class TransactionAssembler
{
    private const int ClosedIdsToRemember = 10_000;

    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeout;
    private readonly Dictionary<string, OpenTransaction> _open = new(StringComparer.Ordinal);
    private readonly Queue<string> _endOrder = new();
    private readonly HashSet<string> _closed = new(StringComparer.Ordinal);
    private readonly Queue<string> _closedOrder = new();
    private readonly List<AssemblerOutput> _ready = [];

    public TransactionAssembler(TimeProvider clock, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _clock = clock;
        _timeout = timeout;
    }

    /// <summary>Transactions that have rows or an END but are not released yet.</summary>
    public int OpenTransactionCount => _open.Count;

    public void Accept(RawChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        if (change.Operation == ChangeOperation.Read || change.TransactionId is null)
        {
            _ready.Add(new SnapshotRow(change));
            return;
        }

        if (_closed.Contains(change.TransactionId))
        {
            _ready.Add(new AbandonedTransaction(change.TransactionId, [change], null, "row arrived after its transaction was closed"));
            return;
        }

        OpenTransaction transaction = GetOrOpen(change.TransactionId);
        transaction.Rows.Add(change);
        ReleaseCompleted();
    }

    public void Accept(TransactionMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);

        if (marker.Status == TransactionStatus.Begin || _closed.Contains(marker.TransactionId))
        {
            _ready.Add(new Discarded(marker.Message.Position));
            return;
        }

        OpenTransaction transaction = GetOrOpen(marker.TransactionId);
        if (transaction.End is not null)
        {
            _ready.Add(new Discarded(marker.Message.Position));
            return;
        }

        transaction.End = marker;
        transaction.EndSeenAt = _clock.GetUtcNow();
        _endOrder.Enqueue(marker.TransactionId);
        ReleaseCompleted();
    }

    /// <summary>Abandons transactions that waited longer than the timeout, then releases what follows.</summary>
    public void ExpireStale()
    {
        DateTimeOffset now = _clock.GetUtcNow();

        while (_endOrder.TryPeek(out string? headId))
        {
            OpenTransaction head = _open[headId];
            if (now - head.EndSeenAt < _timeout)
            {
                break;
            }

            _ = _endOrder.Dequeue();
            Close(head);
            _ready.Add(new AbandonedTransaction(head.Id, head.Rows, head.End, $"incomplete after {_timeout.TotalSeconds:0} s: {head.Describe()}"));
            ReleaseCompleted();
        }

        foreach (OpenTransaction orphan in _open.Values.Where(t => t.End is null && now - t.FirstSeenAt >= _timeout).ToList())
        {
            Close(orphan);
            _ready.Add(new AbandonedTransaction(orphan.Id, orphan.Rows, null, $"no END event after {_timeout.TotalSeconds:0} s"));
        }
    }

    /// <summary>Returns and clears everything released since the last call.</summary>
    public IReadOnlyList<AssemblerOutput> Drain()
    {
        AssemblerOutput[] outputs = [.. _ready];
        _ready.Clear();
        return outputs;
    }

    private OpenTransaction GetOrOpen(string id)
    {
        if (!_open.TryGetValue(id, out OpenTransaction? transaction))
        {
            transaction = new OpenTransaction(id, _clock.GetUtcNow());
            _open[id] = transaction;
        }

        return transaction;
    }

    private void ReleaseCompleted()
    {
        while (_endOrder.TryPeek(out string? headId) && _open[headId].IsComplete)
        {
            _ = _endOrder.Dequeue();
            OpenTransaction head = _open[headId];
            Close(head);
            _ready.Add(new CompletedTransaction(head.Id, [.. head.Rows.OrderBy(row => row.TotalOrder)], head.End!));
        }
    }

    private void Close(OpenTransaction transaction)
    {
        _ = _open.Remove(transaction.Id);
        if (_closed.Add(transaction.Id))
        {
            _closedOrder.Enqueue(transaction.Id);
            if (_closedOrder.Count > ClosedIdsToRemember)
            {
                _ = _closed.Remove(_closedOrder.Dequeue());
            }
        }
    }

    private sealed class OpenTransaction(string id, DateTimeOffset firstSeenAt)
    {
        public string Id { get; } = id;

        public DateTimeOffset FirstSeenAt { get; } = firstSeenAt;

        public List<RawChange> Rows { get; } = [];

        public TransactionMarker? End { get; set; }

        public DateTimeOffset EndSeenAt { get; set; }

        public bool IsComplete =>
            End is not null
            && (End.EventCount is null || Rows.Count == End.EventCount)
            && End.EventCountsByDataCollection.All(expected =>
                Rows.Count(row => row.DataCollection == expected.Key) == expected.Value);

        public string Describe() =>
            End is null
                ? $"{Rows.Count} rows, no END"
                : $"{Rows.Count} of {End.EventCount} rows";
    }
}
