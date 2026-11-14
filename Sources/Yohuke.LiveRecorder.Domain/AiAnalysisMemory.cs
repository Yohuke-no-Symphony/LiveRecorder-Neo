namespace Yohuke.LiveRecorder.Domain;

/// <summary>在私有文件中保存可重新打开的上下文、已提供证据与读取断点。</summary>
public sealed class AiAnalysisMemory
{
    public string JobId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public long Version { get; set; }
    public int SegmentIndex { get; set; } = -1;
    public string InitialPromptJson { get; set; } = "";
    public string ConversationJson { get; set; } = "[]";
    public List<AiEvidence> Evidence { get; set; } = [];
    public List<AiAnalysisRead> Reads { get; set; } = [];
    public List<string> StateReadCursors { get; set; } = [];
    public List<string> ContextReadKeys { get; set; } = [];
    public int NoProgressRounds { get; set; }
    public string? LastToolErrorJson { get; set; }
    public bool ValidationRepairPending { get; set; }
    public string? NoProgressRecoveryFingerprint { get; set; }
    public string? LastRestorationFingerprint { get; set; }
    public int LastRawInputEstimate { get; set; }
    public long? LastPromptTokens { get; set; }
}
