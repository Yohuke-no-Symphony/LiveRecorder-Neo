namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>显式发起或重建一次共享分析。</summary>
public sealed record CreateAiAnalysisRequest(bool Force = false);
