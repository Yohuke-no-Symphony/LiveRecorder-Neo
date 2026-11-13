namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>设置录像是否免于自动清理。</summary>
public sealed record SessionRetentionRequest(bool IsPermanent);
