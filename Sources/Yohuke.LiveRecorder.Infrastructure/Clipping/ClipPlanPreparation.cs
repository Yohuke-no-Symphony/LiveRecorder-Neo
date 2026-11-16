using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Clipping;

/// <summary>Prepares range content identities outside control heartbeats with bounded queueing and cache memory.</summary>
public sealed class ClipPlanPreparation(ClipInputPlanner inputs, ILogger<ClipPlanPreparation> logger) : BackgroundService
{
    private const int MAX_ENTRIES = 32;
    private const int QUEUE_CAPACITY = 16;
    private readonly object gate = new();
    private readonly Dictionary<string, ClipPlanPreparationEntry> entries = new(StringComparer.Ordinal);
    private readonly Channel<ClipPlanPreparationEntry> queue = Channel.CreateBounded<ClipPlanPreparationEntry>(new BoundedChannelOptions(QUEUE_CAPACITY)
    {
        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
    });

    /// <summary>Queues preparation without waiting for disk reads or binding its lifetime to the current heartbeat.</summary>
    public bool Request(MediaJob job)
    {
        var key = Key(job);
        lock (gate)
        {
            RemoveExpired();
            if (entries.TryGetValue(key, out var existing))
            {
                return !existing.Completion.Task.IsCompletedSuccessfully || existing.Completion.Task.Result is not null;
            }
            if (entries.Count >= MAX_ENTRIES)
            {
                var oldest = entries.Values.Where(value => value.CompletedAtUtc is not null)
                    .OrderBy(value => value.CompletedAtUtc).FirstOrDefault();
                if (oldest is null)
                {
                    return false;
                }
                entries.Remove(oldest.Key);
            }
            var entry = new ClipPlanPreparationEntry(key, job);
            entries.Add(key, entry);
            if (queue.Writer.TryWrite(entry))
            {
                return true;
            }
            entries.Remove(key);
            return false;
        }
    }

    /// <summary>Returns a completed preparation without blocking the control connection.</summary>
    public bool TryGetReady(MediaJob job, out ClipJobPlan? plan)
    {
        lock (gate)
        {
            RemoveExpired();
            if (entries.TryGetValue(Key(job), out var entry) && entry.Completion.Task.IsCompletedSuccessfully)
            {
                plan = entry.Completion.Task.Result;
                return plan is not null;
            }
        }
        plan = null;
        return false;
    }

    /// <summary>Reports preparations that should fall back locally until their bounded cache entry expires.</summary>
    public bool HasFailed(MediaJob job)
    {
        lock (gate)
        {
            RemoveExpired();
            return entries.TryGetValue(Key(job), out var entry) && entry.Completion.Task.IsCompletedSuccessfully
                && entry.Completion.Task.Result is null;
        }
    }

    /// <summary>Invalidates a prepared source identity after metadata changes before assignment.</summary>
    public void Invalidate(MediaJob job)
    {
        lock (gate)
        {
            entries.Remove(Key(job));
        }
    }

    /// <summary>Waits for a requested preparation outside the control protocol, for orchestration and acceptance tests.</summary>
    public async Task<ClipJobPlan?> WaitForReadyAsync(MediaJob job, CancellationToken cancellationToken = default)
    {
        Request(job);
        Task<ClipJobPlan?> completion;
        lock (gate)
        {
            if (!entries.TryGetValue(Key(job), out var entry))
            {
                throw new InvalidOperationException("The clipping preparation queue is full.");
            }
            completion = entry.Completion.Task;
        }
        return await completion.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var entry in queue.Reader.ReadAllAsync(stoppingToken))
            {
                ClipJobPlan? plan = null;
                try
                {
                    var preview = await inputs.TryPlanAsync(entry.Job, stoppingToken, false);
                    if (preview is not null && JsonSerializer.SerializeToUtf8Bytes(preview.Assignment, WorkerProtocol.JsonOptions).Length
                        <= WorkerProtocol.MAX_MESSAGE_BYTES - 32768)
                    {
                        plan = await inputs.TryPlanAsync(entry.Job, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    logger.LogWarning("切片 {JobId} 的远端准备失败，将保留本地处理：{ErrorType}", entry.Job.Id, error.GetType().Name);
                }
                lock (gate)
                {
                    entry.CompletedAtUtc = DateTime.UtcNow;
                    entry.Completion.TrySetResult(plan);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            queue.Writer.TryComplete();
            lock (gate)
            {
                foreach (var entry in entries.Values)
                {
                    entry.Completion.TrySetCanceled(stoppingToken);
                }
            }
        }
    }

    private void RemoveExpired()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-2);
        foreach (var entry in entries.Values.Where(value => value.CompletedAtUtc < cutoff).ToArray())
        {
            entries.Remove(entry.Key);
        }
    }

    private static string Key(MediaJob job) => job.Id + ":" + JobOutputManifest.Create(job).InputFingerprint;
}
