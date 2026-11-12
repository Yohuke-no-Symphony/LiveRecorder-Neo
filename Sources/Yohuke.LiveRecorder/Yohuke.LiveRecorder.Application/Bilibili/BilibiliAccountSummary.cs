namespace Yohuke.LiveRecorder.Application.Bilibili;

/// <summary>不包含凭据的采集账号资料。</summary>
public sealed record BilibiliAccountSummary(string Id, long Uid, string UserName, string AvatarUrl, string Status, DateTime? LastValidatedAtUtc);
