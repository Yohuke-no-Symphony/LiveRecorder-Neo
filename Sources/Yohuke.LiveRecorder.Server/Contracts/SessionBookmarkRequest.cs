namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>保存个人标记时必须明确填写的名称和场次时间。</summary>
public sealed class SessionBookmarkRequest
{
    public string? Title { get; set; }
    public double? OffsetMs { get; set; }
}
