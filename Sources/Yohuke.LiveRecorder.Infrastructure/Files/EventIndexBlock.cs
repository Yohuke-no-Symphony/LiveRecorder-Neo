namespace Yohuke.LiveRecorder.Infrastructure.Files;

/// <summary>事件文件中可独立查询的时间范围与字节区间。</summary>
public sealed class EventIndexBlock
{
    internal const int MAX_EVENTS = 128;
    internal const int CURRENT_METADATA_VERSION = 2;

    public int MetadataVersion { get; set; }
    public long StartByte { get; set; }
    public long EndByte { get; set; }
    public double MinMs { get; set; }
    public double MaxMs { get; set; }
    public int Count { get; set; }
    public long LastSequence { get; set; }
    public HashSet<string> Kinds { get; set; } = [];
    public HashSet<string> RedPocketKeys { get; set; } = [];
}
