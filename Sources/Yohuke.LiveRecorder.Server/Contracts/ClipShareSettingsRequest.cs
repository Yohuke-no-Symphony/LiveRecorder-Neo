namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>按当前令牌确认写入切片分享设置。</summary>
public sealed record ClipShareSettingsRequest(string? Token, string Visibility, string? Password = null);
