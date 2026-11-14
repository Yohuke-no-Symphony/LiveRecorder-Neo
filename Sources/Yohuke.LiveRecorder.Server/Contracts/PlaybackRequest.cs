namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>全场时间轴上的播放定位。</summary>
public sealed record PlaybackRequest(double OffsetMs, bool Prefetch = false);
