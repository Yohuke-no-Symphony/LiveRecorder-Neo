using BililiveRecorder.Flv;
using System.Text;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Media;

internal sealed class FlvArchiveTargetProvider : IFlvWriterTargetProvider
{
    private readonly RecordingRequest request;
    private readonly IProgress<MediaRecordingUpdate> progress;
    private readonly string fileStem;
    private int part;
    private double nextOffsetMs;
    private readonly bool collectOutputs;
    private readonly RecordingCommitSnapshotStore? snapshots;
    private readonly TimeProvider timeProvider;
    private string? lastOutputPath;

    internal FlvArchiveTargetProvider(RecordingRequest request, IProgress<MediaRecordingUpdate> progress, bool collectOutputs = true, TimeProvider? timeProvider = null)
    {
        this.request = request;
        this.progress = progress;
        this.collectOutputs = collectOutputs;
        snapshots = request.CaptureCommitSnapshots ? new(request.OutputDirectory) : null;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        var started = request.StartedAtUtc == default ? DateTime.UtcNow : request.StartedAtUtc;
        var sessionStarted = request.SessionStartedAtUtc == default ? started : request.SessionStartedAtUtc;
        nextOffsetMs = Math.Max(0, (started - sessionStarted).TotalMilliseconds);
        var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToHashSet();
        var title = new string(request.Title.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray()).Trim(' ', '.');
        if (title.Length == 0)
        {
            title = "直播录像";
        }
        var safeTitle = new StringBuilder();
        var encodedBytes = 0;
        foreach (var rune in title.EnumerateRunes())
        {
            if (encodedBytes + rune.Utf8SequenceLength > 120)
            {
                break;
            }
            safeTitle.Append(rune.ToString());
            encodedBytes += rune.Utf8SequenceLength;
        }
        title = safeTitle.ToString();
        var localTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(started, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById(request.FileTimeZoneId));
        fileStem = $"{title}_{localTime:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..12]}";
    }

    internal FlvOutputState? Current { get; private set; }
    internal List<FlvArchivedOutput> Outputs { get; } = [];

    public (Stream stream, object? state) CreateOutputStream()
    {
        part++;
        var relative = Path.Combine("录像", $"{fileStem}_part{part:D4}.flv");
        var path = Path.Combine(request.OutputDirectory, relative);
        var indexRelative = Path.ChangeExtension(relative, ".index.jsonl");
        var indexPath = Path.Combine(request.OutputDirectory, indexRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 65536, FileOptions.Asynchronous);
        var index = new StreamWriter(new FileStream(indexPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16384, FileOptions.Asynchronous));
        Current = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            RelativePath = relative,
            IndexRelativePath = indexRelative,
            Path = path,
            IndexPath = indexPath,
            Stream = stream,
            IndexWriter = index,
            StartOffsetMs = nextOffsetMs
        };
        lastOutputPath = path;
        Report("Opened", Current);
        return (stream, Current);
    }

    public Stream CreateAccompanyingTextLogStream()
    {
        var path = Current?.Path ?? lastOutputPath
            ?? Path.Combine(request.OutputDirectory, "录像", fileStem + ".flv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(Path.ChangeExtension(path, ".issues.txt"), FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    internal async Task CommitAsync()
    {
        if (Current is not { } current)
        {
            return;
        }
        await current.Stream.FlushAsync();
        current.CommittedBytes = current.Stream.Length;
        await current.WriteIndexAsync(new()
        {
            Kind = "Committed",
            Offset = current.CommittedBytes,
            TimestampMs = current.DurationMs,
            SessionOffsetMs = current.StartOffsetMs + current.DurationMs,
            ReceivedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            LastMediaReceivedAtUtc = current.LastMediaReceivedAtUtc
        });
        await current.IndexWriter.FlushAsync();
        PublishBoundary(current, isClosed: false);
        Report("Committed", current);
    }

    internal void PrepareClose()
    {
        if (Current is not { } current)
        {
            return;
        }
        current.Stream.Flush();
        current.CommittedBytes = current.Stream.Length;
        current.IndexWriter.WriteLine(System.Text.Json.JsonSerializer.Serialize(new FlvIndexEntry
        {
            Kind = "Committed",
            Offset = current.CommittedBytes,
            TimestampMs = current.DurationMs,
            SessionOffsetMs = current.StartOffsetMs + current.DurationMs,
            ReceivedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            LastMediaReceivedAtUtc = current.LastMediaReceivedAtUtc
        }, MediaJson.Options));
        current.IndexWriter.Flush();
        PublishBoundary(current, isClosed: true);
    }

    internal void Complete(bool prepared = true)
    {
        if (Current is not { } current)
        {
            return;
        }
        current.IndexWriter.Dispose();
        if (collectOutputs)
        {
            Outputs.Add(new FlvArchivedOutput { Path = current.Path, IndexPath = current.IndexPath, StartOffsetMs = current.StartOffsetMs, DurationMs = current.DurationMs });
        }
        nextOffsetMs = current.StartOffsetMs + current.DurationMs;
        if (prepared)
        {
            Report("Closed", current);
        }
        Current = null;
    }

    private void PublishBoundary(FlvOutputState current, bool isClosed)
    {
        current.IndexCommittedBytes = current.IndexWriter.BaseStream.Position;
        if (snapshots is null)
        {
            return;
        }
        current.Stream.Flush(flushToDisk: true);
        ((FileStream)current.IndexWriter.BaseStream).Flush(flushToDisk: true);
        current.CommitSnapshot = snapshots.Write(current, isClosed);
    }

    private void Report(string kind, FlvOutputState state)
    {
        progress.Report(new()
        {
            Kind = kind,
            SegmentId = state.Id,
            RelativePath = state.RelativePath,
            IndexRelativePath = state.IndexRelativePath,
            StartOffsetMs = state.StartOffsetMs,
            DurationMs = state.DurationMs,
            CommittedBytes = state.CommittedBytes,
            IndexCommittedBytes = state.IndexCommittedBytes,
            LastMediaReceivedAtUtc = state.LastMediaReceivedAtUtc,
            Codec = state.Codec,
            CommitSnapshot = state.CommitSnapshot
        });
    }
}
