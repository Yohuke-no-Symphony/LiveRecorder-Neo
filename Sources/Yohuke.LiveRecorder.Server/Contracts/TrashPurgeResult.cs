namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>单个回收站条目的彻底删除结果。</summary>
public sealed class TrashPurgeResult
{
    public string EntryId { get; set; } = "";
    public bool Success { get; set; }
    public string? Error { get; set; }
}
