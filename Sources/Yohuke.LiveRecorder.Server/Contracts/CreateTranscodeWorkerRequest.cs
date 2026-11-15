namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>创建独立身份的转码 Worker。</summary>
public sealed record CreateTranscodeWorkerRequest(string Name);
