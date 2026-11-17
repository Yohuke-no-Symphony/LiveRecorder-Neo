using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

/// <summary>供浏览器持久保存并按来源游标合并的展示事件快照。</summary>
public sealed class SessionEventSnapshot
{
    public string Version { get; init; } = "";
    public bool Reset { get; init; }
    public bool IsLive { get; init; }
    public List<RecordedEventEnvelope> Items { get; init; } = [];
    public List<long> TranslationSequences { get; init; } = [];
    public string? TranslationError { get; set; }
    public string Cursor { get; init; } = "";
}
