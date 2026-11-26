namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>分享卡片展示所需的切片信息。</summary>
public sealed record SharedClipResponse(string Title, double DurationMs, long Bytes, DateTime ExpiresAtUtc,
    PublicUserProfileResponse? Owner = null);
