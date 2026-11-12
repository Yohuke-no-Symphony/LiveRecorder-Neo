namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>实际生成的播放窗口及源时间映射。</summary>
public sealed class PlaybackWindowResult
{
    public string PlaylistPath { get; set; } = "";
    public double SourceStartMs { get; set; }
    public double DurationMs { get; set; }
}
