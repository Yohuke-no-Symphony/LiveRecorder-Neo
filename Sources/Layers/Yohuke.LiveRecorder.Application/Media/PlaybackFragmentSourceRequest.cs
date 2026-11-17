namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>指定用于连续播放的已关闭 AVC/AAC 录制及其提交边界。</summary>
public sealed class PlaybackFragmentSourceRequest
{
    public string SourcePath { get; init; } = "";
    public string IndexPath { get; init; } = "";
    public long CommittedBytes { get; init; }
    public bool IsClosed { get; init; }
}
