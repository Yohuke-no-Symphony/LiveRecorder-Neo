namespace Yohuke.LiveRecorder.Infrastructure.AI;

/// <summary>已按任务来源身份授权的固定字节范围。</summary>
public sealed record AiSourceAsset(string Path, long Length);
