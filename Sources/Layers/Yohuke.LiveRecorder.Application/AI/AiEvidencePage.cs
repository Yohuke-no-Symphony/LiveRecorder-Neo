using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Application.AI;

/// <summary>带有后续游标和截断提示的有界证据页面。</summary>
public sealed class AiEvidencePage
{
    public List<AiEvidence> Items { get; set; } = [];
    public string? NextCursor { get; set; }
    public bool Truncated { get; set; }
}
