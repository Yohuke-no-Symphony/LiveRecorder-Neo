using Yohuke.LiveRecorder.AI.Worker.Storage;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.AI.Worker.Execution;

internal sealed class AiRunningJob
{
    private readonly Lock sync = new();
    private readonly TimeProvider clock;
    private string stage = "Downloading";
    private long sampledAt;

    internal AiRunningJob(AiCheckpointStore store, CancellationToken lifetime, TimeProvider? clock = null)
    {
        Store = store;
        Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        this.clock = clock ?? TimeProvider.System;
        sampledAt = this.clock.GetTimestamp();
        Store.Update(value =>
        {
            foreach (var previous in value.Stages.Where(item => item.State == "Running"))
            {
                previous.State = "Interrupted";
            }
        });
        Stage = "Downloading";
    }

    internal AiCheckpointStore Store { get; }
    internal AiExecutionLease Lease { get; } = new();
    internal CancellationTokenSource Cancellation { get; }
    internal Task Task { get; set; } = Task.CompletedTask;
    internal string Stage
    {
        get
        {
            lock (sync)
            {
                return stage;
            }
        }
        set
        {
            lock (sync)
            {
                RefreshStages();
                Store.Update(checkpoint =>
                {
                    foreach (var previous in checkpoint.Stages.Where(item => item.State == "Running" && item.Id != value))
                    {
                        previous.State = "Completed";
                        previous.Progress = 100;
                        previous.FinishedAtUtc = clock.GetUtcNow().UtcDateTime;
                    }
                    var next = checkpoint.Stages.Single(item => item.Id == value);
                    next.State = "Running";
                    next.StartedAtUtc ??= clock.GetUtcNow().UtcDateTime;
                    next.FinishedAtUtc = null;
                    next.ErrorCode = null;
                });
                stage = value;
            }
        }
    }

    internal void RefreshStages()
    {
        lock (sync)
        {
            var elapsed = clock.GetElapsedTime(sampledAt).TotalMilliseconds;
            sampledAt = clock.GetTimestamp();
            Store.Update(checkpoint =>
            {
                foreach (var current in checkpoint.Stages.Where(item => item.Id == stage && item.State == "Running"))
                {
                    current.ElapsedMs += elapsed;
                    current.Progress = stage == "Analyzing" ? checkpoint.Execution.Progress : 0;
                }
            });
        }
    }

    internal void FinishStage(string state, string? errorCode)
    {
        lock (sync)
        {
            RefreshStages();
            Store.Update(checkpoint =>
            {
                var current = checkpoint.Stages.Single(item => item.Id == stage);
                current.State = state;
                current.FinishedAtUtc = clock.GetUtcNow().UtcDateTime;
                current.ErrorCode = errorCode;
                if (state == "Completed")
                {
                    current.Progress = 100;
                }
            });
        }
    }

    internal WorkerJobProgress Snapshot()
    {
        RefreshStages();
        var checkpoint = Store.Read();
        return new()
        {
            JobId = checkpoint.Assignment.JobId, Attempt = checkpoint.Assignment.Attempt, LeaseToken = checkpoint.Assignment.LeaseToken,
            Stage = Stage, Progress = checkpoint.Execution.Progress,
            Stages = checkpoint.Stages.Select(item => new WorkerStageSnapshot
            {
                Id = item.Id, State = item.State, Progress = item.Progress, StartedAtUtc = item.StartedAtUtc,
                FinishedAtUtc = item.FinishedAtUtc, ElapsedMs = item.ElapsedMs, ErrorCode = item.ErrorCode
            }).ToList()
        };
    }
}
