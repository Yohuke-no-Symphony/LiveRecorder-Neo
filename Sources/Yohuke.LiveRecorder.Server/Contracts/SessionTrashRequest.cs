namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>将指定的录播场次批量移入回收站。</summary>
public sealed class SessionTrashRequest
{
    public List<string> SessionIds { get; set; } = [];
}
