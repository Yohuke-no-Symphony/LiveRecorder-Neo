using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

internal sealed class PlaybackEventSelection(int limit)
{
    private static readonly IComparer<RecordedEventEnvelope> eventOrder = Comparer<RecordedEventEnvelope>.Create((left, right) =>
    {
        var offsetComparison = left.OffsetMs.CompareTo(right.OffsetMs);
        return offsetComparison != 0 ? offsetComparison : left.Sequence.CompareTo(right.Sequence);
    });

    private readonly Dictionary<string, RecordedEventEnvelope> identities = new(StringComparer.Ordinal);
    private readonly SortedSet<RecordedEventEnvelope> ordered = new(eventOrder);

    internal IEnumerable<RecordedEventEnvelope> Items => identities.Values;

    internal bool CanExclude(double maximumOffsetMs, long maximumSequence)
    {
        if (identities.Count < limit)
        {
            return false;
        }
        var oldest = ordered.Min!;
        return maximumOffsetMs < oldest.OffsetMs || maximumOffsetMs == oldest.OffsetMs && maximumSequence <= oldest.Sequence;
    }

    internal void Include(RecordedEventEnvelope item)
    {
        var identity = PlaybackEventIdentity.Key(item);
        if (identities.TryGetValue(identity, out var previous))
        {
            if (eventOrder.Compare(item, previous) <= 0)
            {
                return;
            }
            ordered.Remove(previous);
        }
        else if (identities.Count == limit)
        {
            var oldest = ordered.Min!;
            if (eventOrder.Compare(item, oldest) <= 0)
            {
                return;
            }
            identities.Remove(PlaybackEventIdentity.Key(oldest));
            ordered.Remove(oldest);
        }
        identities[identity] = item;
        ordered.Add(item);
    }
}
