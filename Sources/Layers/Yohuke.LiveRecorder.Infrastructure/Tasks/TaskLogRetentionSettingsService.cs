using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Tasks;

/// <summary>将日志策略发布意图与管理设置同次保存，并幂等补发与清理。</summary>
public sealed class TaskLogRetentionSettingsService(ISettingsStore settings, TaskLogRetentionPolicy retention) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>持久化管理设置；策略暂不可用时保留待发布意图并明确报告保存后的待应用状态。</summary>
    public async Task<ServerSettings> UpdateAsync(Func<ServerSettings, ServerSettings> update, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            long? revision = null;
            using (var lookup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                lookup.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    revision = await retention.RevisionAsync(lookup.Token);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is MongoException or TimeoutException or OperationCanceledException)
                {
                }
            }
            var saved = await settings.UpdateAsync(previous =>
            {
                var days = previous.TaskLogRetentionDays;
                var pending = previous.TaskLogRetentionPending;
                var next = update(previous);
                next.TaskLogRetentionPending = days != next.TaskLogRetentionDays || pending is not null
                    ? new() { Days = next.TaskLogRetentionDays, ExpectedRevision = revision } : null;
                return next;
            }, cancellationToken);
            if (saved.TaskLogRetentionPending is { } change)
            {
                try
                {
                    await PublishAndClearAsync(change, cancellationToken);
                    saved = await settings.GetAsync(cancellationToken);
                }
                catch (Exception error) when (error is MongoException or TimeoutException or OperationCanceledException or InfrastructureException or IOException or UnauthorizedAccessException)
                {
                    throw new InfrastructureException(MessageCode.SETTINGS_TASK_LOG_RETENTION_PENDING, 503);
                }
            }
            return saved;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>后台及重启时补发设置文件中尚未确认的策略意图。</summary>
    public async Task ReplayPendingAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if ((await settings.GetAsync(cancellationToken)).TaskLogRetentionPending is { } change)
            {
                await PublishAndClearAsync(change, cancellationToken);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task PublishAndClearAsync(TaskLogRetentionChange change, CancellationToken cancellationToken)
    {
        var effectiveDays = await retention.PublishAsync(change, cancellationToken);
        await settings.UpdateAsync(current =>
        {
            if (current.TaskLogRetentionPending?.ChangeId == change.ChangeId)
            {
                current.TaskLogRetentionPending = null;
                current.TaskLogRetentionDays = effectiveDays;
            }
            return current;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose() => gate.Dispose();
}
