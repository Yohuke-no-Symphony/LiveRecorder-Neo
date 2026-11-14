namespace Yohuke.LiveRecorder.Server.Realtime;

/// <summary>可在授权撤销时关闭的状态连接。</summary>
public sealed record StatusConnection(string Id, string UserId, long SessionVersion, Action Abort);
