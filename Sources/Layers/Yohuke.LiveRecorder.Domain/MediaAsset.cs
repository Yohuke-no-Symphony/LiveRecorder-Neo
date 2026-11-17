using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Domain;

/// <summary>独立于逻辑场次与时间偏移的已关闭物理媒体身份。</summary>
public sealed class MediaAsset : Entity
{
    public string SourceVersion { get; set; } = "";
    public string SourceRelativePath { get; set; } = "";
    public long SourceLength { get; set; }
    public long SourceWriteTicks { get; set; }
    public long CommittedBytes { get; set; }
    public double DurationMs { get; set; }
    public string? SourceSha256 { get; set; }
    public TranscodeMediaInfo? MediaInfo { get; set; }
    public bool Suspended { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
