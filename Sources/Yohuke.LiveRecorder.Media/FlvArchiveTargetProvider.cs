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
    private string? lastOutputPath;

    internal FlvArchiveTargetProvider(RecordingRequest request, IProgress<MediaRecordingUpdate> progress, bool collectOutputs = true)
    {
        this.request = request;
        this.progress = progress;
        this.collectOutputs = collectOutputs;
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
            ReceivedAtUtc = DateTime.UtcNow
        });
        await current.IndexWriter.FlushAsync();
        Report("Committed", current);
    }

    internal void Complete()
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
        Report("Closed", current);
        Current = null;
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
            Codec = state.Codec
        });
    }
}
