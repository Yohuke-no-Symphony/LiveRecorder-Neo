namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>个人主页中的公开切片分享。</summary>
public sealed record PublicClipShareEntry(string Id, string Token, string Title, double DurationMs,
    long Bytes, DateTime CreatedAtUtc, DateTime ExpiresAtUtc);
