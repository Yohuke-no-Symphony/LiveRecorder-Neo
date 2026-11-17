namespace Yohuke.LiveRecorder.Domain;

/// <summary>保留筛选条件和未读游标，避免把分页窗口误报为逐条读完。</summary>
public sealed class AiAnalysisRead
{
    public string Tool { get; set; } = "";
    public double FromMs { get; set; }
    public double ToMs { get; set; }
    public string? Kind { get; set; }
    public string? Query { get; set; }
    public string? Cursor { get; set; }
    public string? NextCursor { get; set; }
    public bool Truncated { get; set; }
    public List<string> EvidenceIds { get; set; } = [];
}
