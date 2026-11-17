namespace Yohuke.LiveRecorder.Domain;

/// <summary>无需把完整模型上下文放入 Mongo 文档的持久执行清单。</summary>
public sealed class AiAnalysisContextState
{
    public string Phase { get; set; } = "Preparing";
    public List<AiAnalysisSegment> Segments { get; set; } = [];
    public int CurrentSegmentIndex { get; set; }
    public int ContextCompactions { get; set; }
    public int ProgressRecoveries { get; set; }
    public int EffectiveTokenTarget { get; set; }
    public string? ConversationPath { get; set; }
    public long ConversationVersion { get; set; }
}
