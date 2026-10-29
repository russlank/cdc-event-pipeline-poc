using CdcPay.Translation.Raw;

namespace CdcPay.Translation.Assembly;

/// <summary>
/// Groups raw rows into source transactions using Debezium transaction metadata.
/// Rows of one transaction arrive on several topics in any order; a transaction is complete
/// when its END event has arrived and every per-table count it announces is met.
/// Transactions are released in the order of their END events (commit order); a later
/// complete transaction waits for an earlier incomplete one until that times out.
/// Debezium emits a transaction's END only when the next transaction begins, so a transaction
/// without END times out only once a later transaction's BEGIN has been seen: while Connect is
/// down, the last transaction waits for as long as the outage lasts.
/// Redelivery is expected, in two forms. Kafka delivers a message again (same total order):
/// it is discarded. Debezium sends changes again after a task restart (same source position,
/// next total order, and counted in the END): they count toward the END but are not handed
/// out twice. A row or END of a recently released transaction is discarded; a row a released
/// transaction never had is abandoned.
/// Not thread-safe: one consumer loop owns it.
/// </summary>
public sealed class TransactionAssembler
{
    private const int ReleasedToRemember = 10_000;

    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeout;
    private readonly Dictionary<string, OpenTransaction> _open = new(StringComparer.Ordinal);
    private readonly Queue<string> _endOrder = new();
    private readonly Dictionary<string, HashSet<string>> _released = new(StringComparer.Ordinal);
    private readonly Queue<string> _releasedOrder = new();
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

        if (_released.TryGetValue(change.TransactionId, out HashSet<string>? releasedRows))
        {
            _ready.Add(releasedRows.Contains(change.SourcePosition)
                ? new Discarded(change.Message.Position)
                : new AbandonedTransaction(change.TransactionId, [change], null, "row that its already released transaction did not have"));
            return;
        }

        OpenTransaction transaction = GetOrOpen(change.TransactionId);
        if (change.TotalOrder is { } order && !transaction.Orders.Add(order))
        {
            _ready.Add(new Discarded(change.Message.Position));
            return;
        }

        transaction.Count(change);
        if (transaction.Rows.Any(row => row.SourcePosition == change.SourcePosition))
        {
            _ready.Add(new Discarded(change.Message.Position));
        }
        else
        {
            transaction.Rows.Add(change);
        }

        ReleaseCompleted();
    }

    public void Accept(TransactionMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);

        if (_released.ContainsKey(marker.TransactionId))
        {
            _ready.Add(new Discarded(marker.Message.Position));
            return;
        }

        if (marker.Status == TransactionStatus.Begin)
        {
            SupersedeEarlier(marker.TransactionId);
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

        foreach (OpenTransaction orphan in _open.Values.Where(t => t.End is null && t.SupersededAt is { } superseded && now - superseded >= _timeout).ToList())
        {
            Close(orphan);
            _ready.Add(new AbandonedTransaction(orphan.Id, orphan.Rows, null, $"no END event {_timeout.TotalSeconds:0} s after a later transaction began"));
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
            transaction = new OpenTransaction(id);
            _open[id] = transaction;
        }

        return transaction;
    }

    /// <summary>
    /// A later transaction began: earlier open transactions without END start their timeout.
    /// Debezium writes a transaction's END before the next BEGIN on the one-partition transaction
    /// topic, so their END is overdue. Rows cannot tell this: rows of different tables arrive in
    /// any order. SQL Server transaction ids are commit LSNs, which sort in commit order.
    /// </summary>
    private void SupersedeEarlier(string transactionId)
    {
        foreach (OpenTransaction other in _open.Values)
        {
            if (other.End is null && other.SupersededAt is null && string.CompareOrdinal(other.Id, transactionId) < 0)
            {
                other.SupersededAt = _clock.GetUtcNow();
            }
        }
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

    /// <summary>Removes a transaction from the open set and remembers its rows for duplicate detection.</summary>
    private void Close(OpenTransaction transaction)
    {
        _ = _open.Remove(transaction.Id);
        _released[transaction.Id] = [.. transaction.Rows.Select(row => row.SourcePosition)];
        _releasedOrder.Enqueue(transaction.Id);
        if (_releasedOrder.Count > ReleasedToRemember)
        {
            _ = _released.Remove(_releasedOrder.Dequeue());
        }
    }

    private sealed class OpenTransaction(string id)
    {
        private readonly Dictionary<string, long> _countedByDataCollection = new(StringComparer.Ordinal);
        private long _counted;

        public string Id { get; } = id;

        /// <summary>When a message of a later transaction arrived while this one had no END.</summary>
        public DateTimeOffset? SupersededAt { get; set; }

        /// <summary>Distinct changes, without the ones Debezium sent again.</summary>
        public List<RawChange> Rows { get; } = [];

        /// <summary>Every total order seen: what the END counts.</summary>
        public HashSet<long> Orders { get; } = [];

        public TransactionMarker? End { get; set; }

        public DateTimeOffset EndSeenAt { get; set; }

        public bool IsComplete =>
            End is not null
            && (End.EventCount is null || _counted == End.EventCount)
            && End.EventCountsByDataCollection.All(expected =>
                _countedByDataCollection.GetValueOrDefault(expected.Key) == expected.Value);

        /// <summary>Counts a change toward the END, also one Debezium sent again.</summary>
        public void Count(RawChange change)
        {
            _counted++;
            _countedByDataCollection[change.DataCollection] = _countedByDataCollection.GetValueOrDefault(change.DataCollection) + 1;
        }

        public string Describe() =>
            End is null
                ? $"{Rows.Count} rows, no END"
                : $"{_counted} of {End.EventCount} rows";
    }
}
