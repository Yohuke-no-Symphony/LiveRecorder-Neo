using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Tasks;

/// <summary>复用有界日志批次保存明确归属的主站事件，日志故障不影响录制主流程。</summary>
public sealed class TaskEventLogStore(TranscodeWorkerLogStore logs, ILogger<TaskEventLogStore> logger)
{
    /// <summary>保存一个场次事件；固定事件身份允许历史快照重复查询而不产生重复日志。</summary>
    public async Task WriteRecordingAsync(string workerId, string sessionId, string stage, string level, string message,
        DateTime timestampUtc, string? eventId = null, string source = "ServerEvent", CancellationToken cancellationToken = default)
    {
        var identity = eventId ?? Guid.NewGuid().ToString("N");
        var batch = new WorkerLogBatch
        {
            StreamId = TranscodeAssetRegistry.Hash(workerId + ":task-event:" + identity)[..32],
            BatchId = TranscodeAssetRegistry.Hash(workerId + ":task-event-batch:" + identity)[..32],
            Entries = [new()
            {
                Sequence = 1, TimestampUtc = DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc), JobId = sessionId,
                Stage = stage, Level = level, Message = message.Length > 2048 ? message[..2048] : message, Source = source
            }]
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await logs.AppendAsync(workerId, batch, timeout.Token,
                source == "SessionSnapshot" ? timestampUtc.AddDays(TranscodeWorkerLogStore.RETENTION_DAYS) : null);
        }
        catch (OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Task log persistence timed out for recording session {SessionId}.", sessionId);
            }
        }
        catch (Exception error) when (error is MongoException or InfrastructureException)
        {
            logger.LogWarning(error, "Task log persistence will be retried from recording metadata for session {SessionId}.", sessionId);
        }
    }
}
