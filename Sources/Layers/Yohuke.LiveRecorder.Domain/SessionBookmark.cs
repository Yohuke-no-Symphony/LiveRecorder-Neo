namespace Yohuke.LiveRecorder.Domain;

/// <summary>用户在场次真实时间轴上保存的个人定位标记。</summary>
public sealed class SessionBookmark : Entity
{
    public string SessionId { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string Title { get; set; } = "";
    public double OffsetMs { get; set; }
}
