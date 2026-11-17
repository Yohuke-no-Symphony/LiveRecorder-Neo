using BililiveRecorder.Flv;

namespace Yohuke.LiveRecorder.Media;

internal static class FlvTagTiming
{
    internal static long TimestampMs(Tag tag) => unchecked((uint)tag.Timestamp);

    internal static int CompositionOffsetMs(Tag tag)
    {
        if (tag.Type != TagType.Video || tag.BinaryData is not { Length: >= 5 } data)
        {
            return 0;
        }
        var bytes = data.GetBuffer();
        if ((bytes[0] & 0x0F) is not (7 or 12) || bytes[1] != 1)
        {
            return 0;
        }
        var offset = (bytes[2] << 16) | (bytes[3] << 8) | bytes[4];
        return (offset << 8) >> 8;
    }

    internal static long PresentationTimestampMs(Tag tag) => TimestampMs(tag) + CompositionOffsetMs(tag);
}
