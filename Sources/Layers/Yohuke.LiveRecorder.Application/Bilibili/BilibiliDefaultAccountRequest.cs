namespace Yohuke.LiveRecorder.Application.Bilibili;

/// <summary>设置管理员指定的首选采集账号；空值恢复自动选择。</summary>
public sealed record BilibiliDefaultAccountRequest(string? AccountId);
