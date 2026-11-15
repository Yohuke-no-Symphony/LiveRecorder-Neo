using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Execution;

internal sealed class WorkerLogBuffer
{
    internal const int MAX_PENDING_ENTRIES = 1000;
    internal const int MAX_BATCH_ENTRIES = 64;
    internal const int MAX_MESSAGE_LENGTH = 2048;

    private readonly Lock sync = new();
    private readonly Queue<WorkerLogEntry> pending = new();
    private readonly string streamId = Guid.NewGuid().ToString("N");
    private WorkerLogBatch? inFlight;
    private long sequence;
    private long droppedCount;

    internal void Append(WorkerLogEntry entry)
    {
        lock (sync)
        {
            if (pending.Count == MAX_PENDING_ENTRIES)
            {
                pending.Dequeue();
                droppedCount++;
            }
            pending.Enqueue(entry with
            {
                Sequence = ++sequence,
                TimestampUtc = DateTime.UtcNow,
                Message = entry.Message.Length > MAX_MESSAGE_LENGTH ? entry.Message[..MAX_MESSAGE_LENGTH] : entry.Message
            });
        }
    }

    internal WorkerLogBatch? GetBatch()
    {
        lock (sync)
        {
            if (inFlight is not null || pending.Count == 0)
            {
                return inFlight;
            }
            var entries = new List<WorkerLogEntry>(Math.Min(MAX_BATCH_ENTRIES, pending.Count));
            while (entries.Count < MAX_BATCH_ENTRIES && pending.TryDequeue(out var entry))
            {
                entries.Add(entry);
            }
            inFlight = new()
            {
                StreamId = streamId,
                BatchId = Guid.NewGuid().ToString("N"),
                DroppedCount = droppedCount,
                Entries = entries
            };
            droppedCount = 0;
            return inFlight;
        }
    }

    internal void Acknowledge(WorkerLogAcknowledgement? acknowledgement)
    {
        lock (sync)
        {
            if (inFlight is not null && acknowledgement is not null
                && acknowledgement.StreamId == inFlight.StreamId && acknowledgement.BatchId == inFlight.BatchId
                && acknowledgement.ThroughSequence == inFlight.Entries[^1].Sequence)
            {
                inFlight = null;
            }
        }
    }
}
