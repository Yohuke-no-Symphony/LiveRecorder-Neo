namespace Yohuke.LiveRecorder.Application.Bilibili;

/// <summary>管理员绑定采集账号的临时流程状态。</summary>
public sealed record BilibiliLoginSession(string SessionId, string Status, DateTime ExpiresAtUtc, string? QrUrl = null, BilibiliChallenge? Challenge = null, BilibiliAccountSummary? Account = null, string? Message = null, string? VerificationUrl = null);
