namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>物理片段边界处的场次拆分。</summary>
public sealed record SessionSplitRequest(string BeforeSegmentId);
