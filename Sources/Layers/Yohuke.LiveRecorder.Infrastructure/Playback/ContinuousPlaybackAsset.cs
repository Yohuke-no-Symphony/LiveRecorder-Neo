namespace Yohuke.LiveRecorder.Infrastructure.Playback;

/// <summary>已通过源版本校验的连续播放资源及其真实文件内容摘要。</summary>
public sealed record ContinuousPlaybackAsset(string Path, string ContentHash);
