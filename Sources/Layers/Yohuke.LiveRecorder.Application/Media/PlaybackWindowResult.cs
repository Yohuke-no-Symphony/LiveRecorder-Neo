namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>实际生成的播放窗口及源时间映射。</summary>
public sealed class PlaybackWindowResult
{
    public string PlaylistPath { get; set; } = "";
    public double SourceStartMs { get; set; }
    /// <summary>源索引覆盖区间的右端点，用于衔接下一窗口或真实空档。</summary>
    public double SourceEndMs { get; set; }
    /// <summary>窗口是否读取到请求指定的提交字节边界。</summary>
    public bool ReachesCommittedEnd { get; set; }
    public double DurationMs { get; set; }
}
