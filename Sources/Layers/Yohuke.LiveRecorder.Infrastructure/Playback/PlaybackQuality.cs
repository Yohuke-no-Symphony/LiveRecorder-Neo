namespace Yohuke.LiveRecorder.Infrastructure.Playback;

/// <summary>当前物理分段可选择的播放档位。</summary>
public sealed record PlaybackQuality(string Id, string Name, bool Available, int? Height, string State);
