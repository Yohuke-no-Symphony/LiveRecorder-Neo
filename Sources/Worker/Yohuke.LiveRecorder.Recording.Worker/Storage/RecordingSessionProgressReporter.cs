using System.Collections.Concurrent;
using System.Text.Json;
using Yohuke.LiveRecorder.Recording.Contracts;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Recording.Worker.Storage;

internal sealed class RecordingSessionProgressReporter(RecordingSpool spool)
{
    internal const int MAX_BATCH_BYTES = 64 * 1024;
    private readonly ConcurrentDictionary<string, RecordingSessionUploadActivity> uploads = new(StringComparer.Ordinal);
    private string? backlogCursor;

    internal void Uploading(string sessionId) => uploads[sessionId] = new("Uploading");

    internal void Transfer(string sessionId, RecordingSessionTransferProgress transfer) => uploads[sessionId] = new("Uploading", transfer);

    internal void Finalizing(string sessionId) => uploads[sessionId] = new("Finalizing");

    internal void Failed(string sessionId) => uploads[sessionId] = new("Retrying", ErrorCode: "RecordingUploadFailed");

    internal void Uploaded(string sessionId) => uploads.TryRemove(sessionId, out _);

    internal List<RecordingSessionProgress> Batch(IReadOnlyList<RecordingRoomProgress> rooms)
    {
        var active = rooms.Where(value => value.SessionId is not null)
            .ToDictionary(value => value.SessionId!, StringComparer.Ordinal);
        var snapshots = spool.SessionProgress().Select(value =>
        {
            var capture = active.GetValueOrDefault(value.Descriptor.SessionId);
            var upload = uploads.GetValueOrDefault(value.Descriptor.SessionId);
            return value with
            {
                Descriptor = value.Descriptor with
                {
                    Title = Limit(value.Descriptor.Title, 1024), StreamerName = Limit(value.Descriptor.StreamerName, 256),
                    CoverUrl = Limit(value.Descriptor.CoverUrl, 4096)
                },
                CaptureState = value.Descriptor.IsClosed || value.CaptureState == "NoMedia" ? value.CaptureState : capture?.State ?? "Paused",
                UploadState = upload?.State ?? value.UploadState, CurrentTransfer = upload?.Transfer,
                ErrorCode = upload?.ErrorCode ?? capture?.ErrorCode
            };
        }).OrderBy(value => value.Descriptor.SessionId, StringComparer.Ordinal).ToArray();
        var known = snapshots.Select(value => value.Descriptor.SessionId).ToHashSet(StringComparer.Ordinal);
        foreach (var id in uploads.Keys.Where(id => !known.Contains(id)))
        {
            uploads.TryRemove(id, out _);
        }
        var candidates = snapshots.Where(value => active.ContainsKey(value.Descriptor.SessionId)
            || uploads.TryGetValue(value.Descriptor.SessionId, out var activity) && activity.State is "Uploading" or "Finalizing")
            .ToArray();
        var priority = new List<RecordingSessionProgress>();
        var bytes = 2;
        foreach (var candidate in candidates)
        {
            var length = JsonSerializer.SerializeToUtf8Bytes(candidate, WorkerProtocol.JsonOptions).Length + 1;
            if (priority.Count < RecordingSessionProgress.MAX_BATCH_COUNT / 2 + 1 && bytes + length <= MAX_BATCH_BYTES / 2)
            {
                priority.Add(candidate);
                bytes += length;
            }
        }
        var selected = priority.Select(value => value.Descriptor.SessionId).ToHashSet(StringComparer.Ordinal);
        var backlog = snapshots.Where(value => !selected.Contains(value.Descriptor.SessionId)).ToArray();
        var cursor = backlogCursor;
        var ordered = backlog.Where(value => cursor is null || string.CompareOrdinal(value.Descriptor.SessionId, cursor) > 0)
            .Concat(backlog.Where(value => cursor is not null && string.CompareOrdinal(value.Descriptor.SessionId, cursor) <= 0));
        foreach (var candidate in ordered)
        {
            var length = JsonSerializer.SerializeToUtf8Bytes(candidate, WorkerProtocol.JsonOptions).Length + 1;
            if (priority.Count == RecordingSessionProgress.MAX_BATCH_COUNT || bytes + length > MAX_BATCH_BYTES)
            {
                break;
            }
            priority.Add(candidate);
            bytes += length;
            backlogCursor = candidate.Descriptor.SessionId;
        }
        return priority;
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
}
