using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

/// <summary>回放窗口内的事件页。</summary>
public sealed class EventPage
{
    public List<RecordedEventEnvelope> Items { get; set; } = [];
    public long? NextCursor { get; set; }
}
