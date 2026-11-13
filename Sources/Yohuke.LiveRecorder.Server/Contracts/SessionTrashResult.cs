namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>单场录播的回收结果，允许调用方保留失败的选择项。</summary>
public sealed class SessionTrashResult
{
    public string SessionId { get; set; } = "";
    public bool Success { get; set; }
    public string? Error { get; set; }
}
