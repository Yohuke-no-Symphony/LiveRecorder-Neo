namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>从已结束的录制索引创建可按需读取的连续播放源。</summary>
public interface IPlaybackFragmentSourceFactory
{
    string CacheVersion { get; }

    /// <summary>只读取索引并规划分片，不预先封装或读取整份录像。</summary>
    Task<IPlaybackFragmentSource> OpenAsync(PlaybackFragmentSourceRequest request, CancellationToken cancellationToken);
}
