using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Application.Tasks;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;

namespace Yohuke.LiveRecorder.Infrastructure.Tasks;

/// <summary>在任务视图权限内查询指定执行阶段的技术日志。</summary>
public sealed class TaskLogService(TaskHistoryService history, TranscodeWorkerLogStore logs, IMongoContext context, TaskEventLogStore events,
    TaskLogRetentionPolicy retention)
{
    /// <summary>仅向管理员提供已关联到可见任务和当前执行代次的日志。</summary>
    public async Task<TranscodeWorkerLogPage> ReadAsync(string kind, string id, string? stage, long? after, int take,
        TaskHistoryViewer viewer, bool administration, CancellationToken cancellationToken, int? attempt = null)
    {
        if (!viewer.IsAdmin)
        {
            throw new InfrastructureException(MessageCode.REQUEST_FORBIDDEN, 403);
        }
        if (after is < 0 or > TranscodeWorkerLogStore.MAX_CURSOR || take is < 1 or > 500 || stage?.Length > 128 || attempt is < 0)
        {
            throw new InfrastructureException(MessageCode.REQUEST_INVALID);
        }
        var task = await history.GetAsync(kind, id, viewer, administration, cancellationToken);
        if (attempt.HasValue && attempt != task.Attempt)
        {
            throw new InfrastructureException(MessageCode.JOB_STATE_CHANGED, 409);
        }
        if (stage is { Length: > 0 } && !task.Stages.Any(value => value.Id == stage))
        {
            throw new InfrastructureException(MessageCode.REQUEST_INVALID);
        }
        var workerId = task.WorkerId;
        if (kind == "transcode")
        {
            var job = await context.Collection<TranscodeJob>("transcodeJobs").Find(value => value.Id == id).FirstOrDefaultAsync(cancellationToken);
            workerId ??= job?.ResumeWorkerId;
            if (stage == "Processing" && job is not null && job.Stages.All(value => value.State == "Pending")
                && job.Stage is "Completed" or "Failed" or "Cancelled" or "WaitingForWorker" or "")
            {
                stage = null;
            }
        }
        else if (kind == "media")
        {
            var job = await context.Collection<MediaJob>("jobs").Find(value => value.Id == id).FirstOrDefaultAsync(cancellationToken);
            workerId = job?.RemoteWorkerId ?? job?.ResumeWorkerId ?? "local";
            if (stage == "Processing" && job is not null && job.Stages.All(value => value.State == "Pending"))
            {
                stage = null;
            }
        }
        if (string.IsNullOrWhiteSpace(workerId))
        {
            return new();
        }
        if (kind == "recording" && stage is null or "Capturing")
        {
            await RestoreRecordingDiagnosticAsync(workerId, id, cancellationToken);
        }
        return await logs.ReadTaskAsync(workerId, id, kind is "recording" or "ai" || task.Attempt == 0 ? null : task.Attempt,
            stage, after, take, cancellationToken);
    }

    private async Task RestoreRecordingDiagnosticAsync(string workerId, string sessionId, CancellationToken cancellationToken)
    {
        var session = await context.Collection<BroadcastSession>("sessions").Find(value => value.Id == sessionId).FirstOrDefaultAsync(cancellationToken);
        if (session?.EndedAtUtc is null || session.ErrorCode is not { Length: > 0 } code)
        {
            return;
        }
        var timestamp = session.EndedAtUtc.Value;
        if (timestamp == default || !await retention.MayImportSnapshotAsync(timestamp, cancellationToken))
        {
            return;
        }
        var timeout = code == nameof(MessageCode.RECORDING_STREAM_TIMEOUT);
        var message = timeout
            ? "Archived session snapshot: recording ended after the configured interval without valid media; room monitoring continued."
                + (session.ErrorArguments.Count > 0 ? " Interval (minutes): " + session.ErrorArguments[0] + "." : "")
            : "Archived session snapshot: " + code + ". " + (session.Error ?? "");
        await events.WriteRecordingAsync(workerId, sessionId, "Capturing", timeout ? "Information" : "Warning", message,
            timestamp, "recording-snapshot:" + sessionId + ":" + code + ":" + timestamp.Ticks, "SessionSnapshot", cancellationToken);
    }
}
