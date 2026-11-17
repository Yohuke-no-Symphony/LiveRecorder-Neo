namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>持有固定版本的分片计划，供缓存层共享并按需生成媒体。</summary>
public interface IPlaybackFragmentSource
{
    long CommittedBytes { get; }
    IReadOnlyList<PlaybackFragment> Fragments { get; }

    /// <summary>使用调用方提供的资源地址构建不可变 VOD 清单。</summary>
    string CreatePlaylist(Func<int, string, string> assetUri);

    /// <summary>严格按指定序号生成一个独立媒体片及其初始化段。</summary>
    Task<PlaybackWindowResult> BuildFragmentAsync(int sequence, string outputDirectory, CancellationToken cancellationToken);
}
