using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Clipping;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

/// <summary>向主站管理、控制连接和媒体传输端点提供一致的转码边界。</summary>
public sealed class TranscodingCoordinator(TranscodeWorkerService workers, TranscodeJobScheduler scheduler,
    TranscodeTransferService transfers, TranscodeRenditionCatalog catalog, TranscodeWorkerLogStore logs, ClippingJobScheduler clipping,
    RecordingJobScheduler recording)
{
    /// <summary>读取已登记节点。</summary>
    public Task<IReadOnlyList<TranscodeWorker>> ListWorkersAsync(CancellationToken cancellationToken = default) => workers.ListAsync(cancellationToken);
    /// <summary>创建节点并交付一次性可见的凭据。</summary>
    public Task<(TranscodeWorker Worker, string Secret)> CreateWorkerAsync(string name, CancellationToken cancellationToken = default) => workers.CreateAsync(name, cancellationToken);
    /// <summary>Creates a Worker registered for the requested task kind.</summary>
    public Task<(TranscodeWorker Worker, string Secret)> CreateWorkerAsync(string name, string kind, CancellationToken cancellationToken = default) => workers.CreateAsync(name, kind, cancellationToken);
    /// <summary>保存节点配置的指定版本。</summary>
    public Task<TranscodeWorker> UpdateWorkerAsync(string id, string name, bool enabled, WorkerConfiguration configuration, int expectedRevision, CancellationToken cancellationToken = default) => workers.UpdateAsync(id, name, enabled, configuration, expectedRevision, cancellationToken);
    /// <summary>轮换节点凭据。</summary>
    public Task<string> RotateKeyAsync(string id, CancellationToken cancellationToken = default) => workers.RotateAsync(id, cancellationToken);
    /// <summary>删除节点并释放租约。</summary>
    public Task DeleteWorkerAsync(string id, CancellationToken cancellationToken = default) => workers.DeleteAsync(id, cancellationToken);
    /// <summary>验证节点凭据。</summary>
    public Task<bool> AuthenticateAsync(string workerId, string secret, CancellationToken cancellationToken = default) => workers.AuthenticateAsync(workerId, secret, cancellationToken);
    /// <summary>完成控制连接握手。</summary>
    public async Task<WorkerWelcome> ConnectAsync(WorkerHello hello, CancellationToken cancellationToken = default) =>
        (await workers.ConnectAsync(hello, cancellationToken)) with { LogProtocolVersion = 1 };
    /// <summary>同步运行状态、续租和任务派发。</summary>
    public async Task<WorkerState> HeartbeatAsync(string workerId, WorkerHeartbeat heartbeat, CancellationToken cancellationToken = default)
    {
        var worker = await workers.RequireAsync(workerId, cancellationToken, true);
        if (worker.Kind == "Recording")
        {
            return await recording.HeartbeatAsync(workerId, heartbeat, cancellationToken);
        }
        return worker.Kind == "Clipping"
            ? await clipping.HeartbeatAsync(workerId, heartbeat, cancellationToken)
            : await scheduler.HeartbeatAsync(workerId, heartbeat, cancellationToken);
    }
    /// <summary>Processes a control heartbeat while binding dispatch to the connection's authenticated credential generation.</summary>
    public async Task<WorkerState> HeartbeatAsync(string workerId, string secret, WorkerHeartbeat heartbeat,
        CancellationToken cancellationToken = default)
    {
        var worker = await workers.RequireAsync(workerId, cancellationToken, true);
        var expectedHash = TranscodeWorkerService.SecretHash(secret);
        if (worker.Kind == "Recording")
        {
            return await recording.HeartbeatAsync(workerId, heartbeat, cancellationToken, expectedHash);
        }
        return worker.Kind == "Clipping"
            ? await clipping.HeartbeatAsync(workerId, heartbeat, cancellationToken, expectedHash)
            : await scheduler.HeartbeatAsync(workerId, heartbeat, cancellationToken, expectedHash);
    }
    /// <summary>持久化独立日志批次，完成发布后返回精确确认。</summary>
    public Task<WorkerLogAcknowledgement> AppendWorkerLogsAsync(string workerId, WorkerLogBatch batch, CancellationToken cancellationToken = default) =>
        logs.AppendAsync(workerId, batch, cancellationToken);
    /// <summary>读取指定节点的最近日志或游标之后的增量内容。</summary>
    public Task<TranscodeWorkerLogPage> ReadWorkerLogsAsync(string workerId, long? after, int take, CancellationToken cancellationToken = default) =>
        logs.ReadAsync(workerId, after, take, cancellationToken);
    /// <summary>读取近期转码任务。</summary>
    public Task<IReadOnlyList<TranscodeJob>> ListJobsAsync(string? sessionId, CancellationToken cancellationToken = default) => scheduler.ListAsync(sessionId, cancellationToken);
    /// <summary>为结束场次补齐选中的档位。</summary>
    public Task<int> QueueSessionsAsync(IReadOnlyCollection<string> sessionIds, IReadOnlyCollection<string>? qualityIds, bool force, CancellationToken cancellationToken = default) => scheduler.QueueAsync(sessionIds, qualityIds, force, cancellationToken);
    /// <summary>取消执行权。</summary>
    public Task CancelJobAsync(string id, CancellationToken cancellationToken = default) => scheduler.CancelAsync(id, cancellationToken);
    /// <summary>重试有效输入版本的任务。</summary>
    public Task RetryJobAsync(string id, CancellationToken cancellationToken = default) => scheduler.RetryAsync(id, cancellationToken);
    /// <summary>授权读取当前输入版本。</summary>
    public Task<TranscodeInput> GetInputAsync(string workerId, string jobId, int attempt, string leaseToken, CancellationToken cancellationToken = default) => transfers.GetInputAsync(workerId, jobId, attempt, leaseToken, cancellationToken);
    /// <summary>查询当前上传文件的已提交长度。</summary>
    public Task<long> GetUploadOffsetAsync(string workerId, string jobId, int attempt, string leaseToken, string fileName, long length, CancellationToken cancellationToken = default) => transfers.GetUploadOffsetAsync(workerId, jobId, attempt, leaseToken, fileName, length, cancellationToken);
    /// <summary>提交有界数据块。</summary>
    public Task<long> UploadAsync(string workerId, string jobId, int attempt, string leaseToken, string fileName, long offset, long length, Stream content, CancellationToken cancellationToken = default) => transfers.UploadAsync(workerId, jobId, attempt, leaseToken, fileName, offset, length, content, cancellationToken);
    /// <summary>验证完整成品并原子发布。</summary>
    public Task CompleteAsync(string workerId, string jobId, int attempt, string leaseToken, TranscodeOutputManifest manifest, CancellationToken cancellationToken = default) => transfers.CompleteAsync(workerId, jobId, attempt, leaseToken, manifest, cancellationToken);
    /// <summary>提交未产出媒体的终态。</summary>
    public Task FinishAsync(string workerId, string jobId, int attempt, string leaseToken, WorkerJobResult result, CancellationToken cancellationToken = default) => transfers.FinishAsync(workerId, jobId, attempt, leaseToken, result, cancellationToken);
    /// <summary>读取当前片段可复用的已验证播放产物。</summary>
    public Task<IReadOnlyList<PlaybackRendition>> GetRenditionsAsync(string sessionId, string segmentId, CancellationToken cancellationToken = default) => catalog.GetAsync(sessionId, segmentId, cancellationToken);
    /// <summary>按当前场次引用重新授权成品文件。</summary>
    public Task<RenditionAsset> ResolveRenditionAssetAsync(string sessionId, string segmentId, string renditionId, string fileName, CancellationToken cancellationToken = default) => catalog.ResolveAsync(sessionId, segmentId, renditionId, fileName, cancellationToken);
}
