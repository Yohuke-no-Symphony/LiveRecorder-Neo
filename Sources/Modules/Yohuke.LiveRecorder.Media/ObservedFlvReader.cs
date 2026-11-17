using BililiveRecorder.Flv;

namespace Yohuke.LiveRecorder.Media;

internal sealed class ObservedFlvReader : IFlvTagReader
{
    private readonly IFlvTagReader inner;
    private readonly Dictionary<long, FlvSourceTagInfo> observations;
    private readonly TimeProvider timeProvider;

    internal ObservedFlvReader(IFlvTagReader inner, Dictionary<long, FlvSourceTagInfo> observations, TimeProvider? timeProvider = null)
    {
        this.inner = inner;
        this.observations = observations;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Tag?> PeekTagAsync(CancellationToken token)
    {
        var tag = await inner.PeekTagAsync(token);
        Observe(tag);
        return tag;
    }

    public async Task<Tag?> ReadTagAsync(CancellationToken token)
    {
        var tag = await inner.ReadTagAsync(token);
        Observe(tag);
        return tag;
    }

    private void Observe(Tag? tag)
    {
        if (tag is null)
        {
            return;
        }
        observations.TryAdd(tag.Index, new(tag.Timestamp, timeProvider.GetUtcNow().UtcDateTime));
        if (tag.Type == TagType.Video && tag.BinaryData is { Length: > 0 } data)
        {
            var first = data.GetBuffer()[0];
            if ((first & 0x80) != 0)
            {
                throw new NotSupportedException("Enhanced FLV is not supported by the archival repair engine. Select an AVC stream.");
            }
            if ((first & 0x0F) is not (7 or 12))
            {
                throw new NotSupportedException("The FLV video codec is not AVC or the supported codec-12 HEVC variant.");
            }
        }
    }

    public void Dispose()
    {
        inner.Dispose();
    }
}
