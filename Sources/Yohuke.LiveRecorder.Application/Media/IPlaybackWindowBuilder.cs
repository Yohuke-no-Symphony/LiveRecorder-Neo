namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>按需创建可共享的播放窗口。</summary>
public interface IPlaybackWindowBuilder
{
    /// <summary>只读取目标附近的媒体并构建播放产物。</summary>
    Task<PlaybackWindowResult> BuildAsync(PlaybackWindowRequest request, CancellationToken cancellationToken);
}
