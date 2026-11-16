namespace Yohuke.LiveRecorder.Infrastructure.Files;

/// <summary>在无需重新解析归档时返回条件验证结果，否则附带完整或增量快照。</summary>
public sealed class SessionEventSnapshotResult
{
    public string EntityTag { get; init; } = "";
    public bool NotModified { get; init; }
    public SessionEventSnapshot? Snapshot { get; init; }
}
