namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>需要彻底删除的回收站条目标识。</summary>
public sealed class TrashPurgeRequest
{
    public List<string> EntryIds { get; set; } = [];
}
