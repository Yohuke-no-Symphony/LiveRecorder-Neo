using BililiveRecorder.Flv;
using BililiveRecorder.Flv.Amf;

namespace Yohuke.LiveRecorder.Media;

internal sealed class IndexedFlvTagWriter : IFlvTagWriter
{
    private readonly IFlvTagWriter inner;
    private readonly FlvArchiveTargetProvider target;
    private readonly Dictionary<long, FlvSourceTagInfo> sourceTags;
    private ScriptTagBody? currentMetadata;

    internal IndexedFlvTagWriter(IFlvTagWriter inner, FlvArchiveTargetProvider target, Dictionary<long, FlvSourceTagInfo> sourceTags)
    {
        this.inner = inner;
        this.target = target;
        this.sourceTags = sourceTags;
    }

    public long FileSize => inner.FileSize;
    public object? State => inner.State;

    public Task CreateNewFile()
    {
        currentMetadata = null;
        return inner.CreateNewFile();
    }

    internal async Task PrepareCloseAsync()
    {
        if (target.Current is not { } current)
        {
            return;
        }
        target.FinalizeDuration();
        if (currentMetadata?.GetMetadataValue() is { } value)
        {
            value["duration"] = (ScriptDataNumber)(current.MetadataDurationMs / 1000d);
            await inner.OverwriteMetadata(currentMetadata);
        }
    }

    public bool CloseCurrentFile()
    {
        var closed = false;
        var prepared = false;
        try
        {
            target.PrepareClose();
            prepared = true;
        }
        finally
        {
            closed = inner.CloseCurrentFile();
            if (closed)
            {
                target.Complete(prepared);
            }
        }
        return closed;
    }

    public async Task WriteTag(Tag tag)
    {
        if (tag.Type == TagType.Script || tag.IsHeader())
        {
            tag.Timestamp = 0;
        }
        var offset = inner.FileSize;
        var current = target.Current!;
        sourceTags.TryGetValue(tag.Index, out var source);
        var kind = tag.IsHeader()
            ? tag.Type == TagType.Video ? "VideoHeader" : "AudioHeader"
            : tag.Type == TagType.Video && tag.IsKeyframeData() ? "Keyframe" : "";
        if (kind.Length > 0)
        {
            await current.WriteIndexAsync(new()
            {
                Kind = kind,
                Offset = offset,
                Size = tag.Size,
                TimestampMs = FlvTagTiming.TimestampMs(tag),
                OriginalTimestampMs = source?.Timestamp,
                SourceTagIndex = source is null ? null : tag.Index,
                ReceivedAtUtc = source?.ReceivedAtUtc ?? DateTime.UtcNow,
                SessionOffsetMs = current.StartOffsetMs + FlvTagTiming.TimestampMs(tag),
                HeaderData = tag.IsHeader() ? tag.BinaryData?.ToArray() : null
            });
        }

        if (tag.Type == TagType.Video && tag.BinaryData is { Length: > 0 } video)
        {
            current.Codec = (video.GetBuffer()[0] & 0x0F) == 12 ? "hevc" : "avc";
        }
        if (FlvAudioTiming.FrameDurationMs(tag) is { } audioDuration)
        {
            current.AudioFrameDurationMs = audioDuration;
        }
        if (tag.Type == TagType.Script && tag.ScriptData?.GetMetadataValue() is { } script
            && script.TryGetValue("framerate", out var rate) && rate is ScriptDataNumber { Value: > 0 } frameRate)
        {
            current.FrameDurationMs = 1000d / frameRate.Value;
        }
        if (tag.IsData())
        {
            var timestamp = FlvTagTiming.TimestampMs(tag);
            var presentation = FlvTagTiming.PresentationTimestampMs(tag);
            if (tag.Type == TagType.Video)
            {
                current.FirstVideoPresentationMs ??= presentation;
                var difference = timestamp - current.LastVideoTimestampMs;
                if (current.LastVideoTimestampMs >= 0 && difference is > 0 and < 250)
                {
                    current.FrameDurationMs = difference;
                }
                current.LastVideoTimestampMs = timestamp;
            }
            else if (tag.Type == TagType.Audio)
            {
                current.FirstAudioPresentationMs ??= presentation;
                current.LastAudioTimestampMs = timestamp;
            }
            var duration = presentation + (tag.Type == TagType.Video ? current.FrameDurationMs : current.AudioFrameDurationMs);
            current.EstimatedEndMs = Math.Max(current.EstimatedEndMs, duration);
            var watermark = current.LastVideoTimestampMs >= 0 && current.LastAudioTimestampMs >= 0
                ? Math.Min(current.LastVideoTimestampMs, current.LastAudioTimestampMs)
                : Math.Max(current.LastVideoTimestampMs, current.LastAudioTimestampMs);
            current.DurationMs = Math.Max(current.DurationMs, watermark);
        }
        await inner.WriteTag(tag);
        if (!tag.IsHeader() && tag.Type is TagType.Audio or TagType.Video && source is not null
            && (current.LastMediaReceivedAtUtc is null || source.ReceivedAtUtc > current.LastMediaReceivedAtUtc))
        {
            current.LastMediaReceivedAtUtc = source.ReceivedAtUtc;
        }
    }

    public Task OverwriteMetadata(ScriptTagBody metadata)
    {
        currentMetadata = metadata;
        return inner.OverwriteMetadata(metadata);
    }

    public Task WriteAccompanyingTextLog(double lastTagDuration, string message)
    {
        return inner.WriteAccompanyingTextLog(lastTagDuration, message);
    }

    public void Dispose()
    {
        CloseCurrentFile();
        inner.Dispose();
    }
}
