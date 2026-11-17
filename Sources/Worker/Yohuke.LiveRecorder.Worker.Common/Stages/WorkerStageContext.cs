using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Worker.Common.Stages;

internal abstract class WorkerStageContext(List<WorkerStageSnapshot> stages, Action persist,
    Action<WorkerStageDefinition, WorkerStageSnapshot, IReadOnlyList<WorkerStageSnapshot>> publish, TimeProvider? timeProvider = null)
{
    private readonly Lock sync = new();
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private WorkerStageDefinition? current;
    private long started;
    private double elapsedBefore;

    internal IReadOnlyList<WorkerStageSnapshot> Snapshot()
    {
        lock (sync)
        {
            RefreshElapsed();
            return stages.Select(Clone).ToArray();
        }
    }

    internal void Initialize(IEnumerable<WorkerStageDefinition> definitions)
    {
        lock (sync)
        {
            var selected = definitions.ToArray();
            if (!stages.Select(value => value.Id).SequenceEqual(selected.Select(value => value.Id), StringComparer.Ordinal))
            {
                stages.Clear();
                stages.AddRange(selected.Select(value => new WorkerStageSnapshot { Id = value.Id }));
                persist();
            }
        }
    }

    internal WorkerStageSnapshot Get(string id)
    {
        lock (sync)
        {
            return Clone(stages.Single(value => value.Id == id));
        }
    }

    internal void Begin(WorkerStageDefinition definition)
    {
        lock (sync)
        {
            current = definition;
            var stage = stages.Single(value => value.Id == definition.Id);
            elapsedBefore = stage.ElapsedMs;
            started = time.GetTimestamp();
            stage.State = "Running";
            stage.StartedAtUtc ??= time.GetUtcNow().UtcDateTime;
            stage.FinishedAtUtc = null;
            stage.Error = null;
            stage.ErrorCode = null;
            stage.Substate = null;
            stage.Progress = 0;
            stage.Activity = null;
            persist();
            Publish(stage);
        }
    }

    internal void Report(double progress, WorkerJobActivity? activity = null, string? substate = null)
    {
        lock (sync)
        {
            if (current is null)
            {
                return;
            }
            var stage = stages.Single(value => value.Id == current.Id);
            stage.Progress = double.IsFinite(progress) ? Math.Clamp(progress, 0, 100) : 0;
            stage.Activity = activity is null ? null : activity with { };
            stage.Substate = substate;
            RefreshElapsed();
            Publish(stage);
        }
    }

    internal void End(string state, string? error = null)
    {
        lock (sync)
        {
            var stage = stages.Single(value => value.Id == current!.Id);
            RefreshElapsed();
            stage.State = state;
            stage.FinishedAtUtc = time.GetUtcNow().UtcDateTime;
            stage.Error = error is { Length: > 4000 } ? error[..4000] : error;
            if (state == "Completed")
            {
                stage.Progress = 100;
            }
            persist();
            Publish(stage);
            current = null;
        }
    }

    internal void Restored(WorkerStageDefinition definition)
    {
        lock (sync)
        {
            var stage = stages.Single(value => value.Id == definition.Id);
            stage.Substate = "Restored";
            publish(definition, Clone(stage), stages.Select(Clone).ToArray());
        }
    }

    internal void InvalidateFrom(string id)
    {
        lock (sync)
        {
            var index = stages.FindIndex(value => value.Id == id);
            if (index < 0)
            {
                throw new InvalidOperationException("A stage requested an unknown restart boundary.");
            }
            for (; index < stages.Count; index++)
            {
                var stage = stages[index];
                stage.State = "Pending";
                stage.Progress = 0;
                stage.FinishedAtUtc = null;
                stage.Activity = null;
                stage.Error = null;
                stage.ErrorCode = null;
                stage.Substate = null;
            }
            persist();
        }
    }

    internal void Save()
    {
        lock (sync)
        {
            RefreshElapsed();
            persist();
        }
    }

    internal void Pulse()
    {
        lock (sync)
        {
            if (current is not null)
            {
                RefreshElapsed();
                Publish(stages.Single(value => value.Id == current.Id));
            }
        }
    }

    private void Publish(WorkerStageSnapshot stage) => publish(current!, Clone(stage), stages.Select(Clone).ToArray());

    private void RefreshElapsed()
    {
        if (current is not null)
        {
            stages.Single(value => value.Id == current.Id).ElapsedMs = elapsedBefore + time.GetElapsedTime(started).TotalMilliseconds;
        }
    }

    private static WorkerStageSnapshot Clone(WorkerStageSnapshot value) => value with { Activity = value.Activity is null ? null : value.Activity with { } };
}
