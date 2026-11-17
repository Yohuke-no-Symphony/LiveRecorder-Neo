using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

/// <summary>包含定位时刻最近消息、未来预取和关联红包原始阶段的回放窗口。</summary>
public sealed class PlaybackEventWindow
{
    public double FromMs { get; init; }
    public double ToMs { get; init; }
    public List<RecordedEventEnvelope> Items { get; init; } = [];
}
