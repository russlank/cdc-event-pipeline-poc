using CdcPay.Translation.Raw;

namespace CdcPay.Translation.Assembly;

/// <summary>
/// Decides which offsets are safe to commit. Per partition the commit point is the lowest
/// offset still in progress, or the next offset after everything seen when nothing is in
/// progress. Messages before the commit point are never needed again after a restart.
/// Not thread-safe: one consumer loop owns it.
/// </summary>
public sealed class OffsetTracker
{
    private readonly Dictionary<(string Topic, int Partition), PartitionState> _partitions = [];

    /// <summary>Records a consumed message as in progress.</summary>
    public void Register(KafkaPosition position)
    {
        PartitionState state = State(position);
        _ = state.InProgress.Add(position.Offset);
        state.Highest = Math.Max(state.Highest, position.Offset);
    }

    /// <summary>Records that a message is fully handled.</summary>
    public void Complete(KafkaPosition position) => _ = State(position).InProgress.Remove(position.Offset);

    /// <summary>The offset to commit per partition (Kafka semantics: the next offset to read).</summary>
    public IReadOnlyDictionary<(string Topic, int Partition), long> CommitPoints() =>
        _partitions.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.InProgress.Count > 0 ? entry.Value.InProgress.Min : entry.Value.Highest + 1);

    private PartitionState State(KafkaPosition position)
    {
        (string, int) key = (position.Topic, position.Partition);
        if (!_partitions.TryGetValue(key, out PartitionState? state))
        {
            state = new PartitionState();
            _partitions[key] = state;
        }

        return state;
    }

    private sealed class PartitionState
    {
        public SortedSet<long> InProgress { get; } = [];

        public long Highest { get; set; } = -1;
    }
}
