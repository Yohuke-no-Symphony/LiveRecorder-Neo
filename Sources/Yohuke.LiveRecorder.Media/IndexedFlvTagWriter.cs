using BililiveRecorder.Flv;
using BililiveRecorder.Flv.Amf;

namespace Yohuke.LiveRecorder.Media;

internal sealed class IndexedFlvTagWriter : IFlvTagWriter
{
    private readonly IFlvTagWriter inner;
    private readonly FlvArchiveTargetProvider target;
    private readonly Dictionary<long, FlvSourceTagInfo> sourceTags;

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
        return inner.CreateNewFile();
    }

    public bool CloseCurrentFile()
    {
        var closed = inner.CloseCurrentFile();
        if (closed)
        {
            target.Complete();
        }
        return closed;
    }

    public async Task WriteTag(Tag tag)
    {
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
                TimestampMs = tag.Timestamp,
                OriginalTimestampMs = source?.Timestamp,
                SourceTagIndex = source is null ? null : tag.Index,
                ReceivedAtUtc = source?.ReceivedAtUtc ?? DateTime.UtcNow,
                SessionOffsetMs = current.StartOffsetMs + tag.Timestamp,
                HeaderData = tag.IsHeader() ? tag.BinaryData?.ToArray() : null
            });
        }

        if (tag.Type == TagType.Video && tag.BinaryData is { Length: > 0 } video)
        {
            current.Codec = (video.GetBuffer()[0] & 0x0F) == 12 ? "hevc" : "avc";
        }
        if (!tag.IsHeader() && tag.Type is TagType.Audio or TagType.Video)
        {
            if (tag.Type == TagType.Video)
            {
                var difference = tag.Timestamp - current.LastVideoTimestampMs;
                if (current.LastVideoTimestampMs >= 0 && difference is > 0 and < 250)
                {
                    current.FrameDurationMs = difference;
                }
                current.LastVideoTimestampMs = tag.Timestamp;
            }
            var duration = tag.Timestamp + (tag.Type == TagType.Video ? current.FrameDurationMs : 1024d / 48);
            current.DurationMs = Math.Max(current.DurationMs, duration);
        }
        await inner.WriteTag(tag);
    }

    public Task OverwriteMetadata(ScriptTagBody metadata)
    {
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
