namespace Yohuke.LiveRecorder.Application.Media;

/// <summary>成员切片或管理员媒体维护请求。</summary>
public sealed class CreateMediaJobRequest
{
    public string Kind { get; set; } = "Clip";
    public string SessionId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Mode { get; set; } = "Exact";
    public double StartMs { get; set; }
    public double EndMs { get; set; }
    public List<string> SegmentIds { get; set; } = [];
}
