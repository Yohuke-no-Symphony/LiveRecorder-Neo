namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>从已提交媒体位置生成独立播放窗口。</summary>
public sealed class PlaybackWindowRequest
{
    public string SourcePath { get; set; } = "";
    public string IndexPath { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public double SourceStartMs { get; set; }
    public double DurationMs { get; set; } = PlaybackWindowOptions.DEFAULT_DURATION_MS;
    public long CommittedBytes { get; set; }
    public bool IsClosed { get; set; }
    public bool Transcode { get; set; }
}
