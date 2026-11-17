namespace Yohuke.LiveRecorder.Infrastructure.Playback;

/// <summary>不可变播放代次的文件清单。</summary>
public sealed class PlaybackCacheEntry
{
    public string SessionId { get; set; } = "";
    public string SegmentId { get; set; } = "";
    public long SourceBytes { get; set; }
    public string Generation { get; set; } = "";
    public double StartOffsetMs { get; set; }
    public double EndOffsetMs { get; set; }
    public DateTime BuiltAtUtc { get; set; }
    public List<PlaybackWindowFileMetadata> Files { get; set; } = [];
}
