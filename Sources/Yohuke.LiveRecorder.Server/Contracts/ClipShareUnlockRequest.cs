namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>接收者提交的切片分享提取密码。</summary>
public sealed record ClipShareUnlockRequest(string? Password);
