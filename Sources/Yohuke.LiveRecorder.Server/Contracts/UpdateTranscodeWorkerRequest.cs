using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>带配置版本校验的 Worker 管理请求。</summary>
public sealed record UpdateTranscodeWorkerRequest(string Name, bool Enabled, WorkerConfiguration Configuration, int ExpectedRevision);
