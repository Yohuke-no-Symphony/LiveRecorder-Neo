using System.Globalization;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Scheduling;

internal sealed class WorkerJobReporter(WorkerRunningJob job, Action<WorkerLogEntry> log, TimeProvider? timeProvider = null)
{
    private readonly Lock sync = new();
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private WorkerTransferProgress? transfer;
    private long stageStarted;
    private long sampleStarted;
    private long sampleBytes;
    private long lastPublished;
    private long lastEncoding;
    private double? bytesPerSecond;
    private bool stopped;

    internal void Stop()
    {
        lock (sync)
        {
            stopped = true;
        }
    }

    internal void Start(string stage, double progress, string message, string? encoder = null)
    {
        lock (sync)
        {
            transfer = null;
            bytesPerSecond = null;
            stageStarted = time.GetTimestamp();
            job.Update(stage, progress, encoder);
            Write(job.Snapshot(), message, "Information");
        }
    }

    internal void Message(string message, string level = "Information")
    {
        lock (sync)
        {
            Write(job.Snapshot(), message, level);
        }
    }

    internal void Transfer(WorkerTransferProgress value)
    {
        lock (sync)
        {
            if (stopped || job.Snapshot().Stage is not ("Downloading" or "Uploading"))
            {
                return;
            }
            var now = time.GetTimestamp();
            var force = transfer is null || value.BytesTransferred == value.TotalBytes
                || value.FilesCompleted != transfer.Value.FilesCompleted;
            if (transfer is null || value.BytesTransferred < sampleBytes)
            {
                sampleStarted = now;
                sampleBytes = value.BytesTransferred;
                bytesPerSecond = null;
            }
            transfer = value;
            if (force || time.GetElapsedTime(lastPublished, now) >= TimeSpan.FromMilliseconds(200))
            {
                UpdateTransfer(now);
            }
        }
    }

    internal void Encoding(WorkerEncodingProgress value, double totalDurationMs)
    {
        lock (sync)
        {
            if (stopped)
            {
                return;
            }
            lastEncoding = time.GetTimestamp();
            var processed = Math.Clamp(value.ProcessedDurationMs, 0, totalDurationMs);
            var fraction = totalDurationMs > 0 ? processed / totalDurationMs : 0;
            job.Update("Encoding", 15 + fraction * 65, activity: new()
            {
                ProcessedDurationMs = processed,
                TotalDurationMs = totalDurationMs,
                FramesPerSecond = value.FramesPerSecond,
                Speed = value.Speed,
                EstimatedSecondsRemaining = value.Speed is > 0 ? (totalDurationMs - processed) / (1000 * value.Speed.Value) : null
            });
        }
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), time);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            Pulse();
        }
    }

    internal void Pulse()
    {
        lock (sync)
        {
            if (stopped)
            {
                return;
            }
            var now = time.GetTimestamp();
            if (transfer is not null)
            {
                UpdateTransfer(now);
            }
            var snapshot = job.Snapshot();
            if (snapshot.Stage == "Encoding" && snapshot.Activity is { } activity
                && time.GetElapsedTime(lastEncoding, now) >= TimeSpan.FromSeconds(5))
            {
                job.Update(snapshot.Stage, snapshot.Progress, activity: activity with
                {
                    FramesPerSecond = 0, Speed = 0, EstimatedSecondsRemaining = null
                });
                snapshot = job.Snapshot();
            }
            Write(snapshot, Describe(snapshot, time.GetElapsedTime(stageStarted, now).TotalSeconds), "Information");
        }
    }

    private void UpdateTransfer(long now)
    {
        var value = transfer!.Value;
        var elapsed = time.GetElapsedTime(sampleStarted, now).TotalSeconds;
        if (elapsed >= (bytesPerSecond.HasValue ? 5 : 1))
        {
            bytesPerSecond = Math.Max(0, value.BytesTransferred - sampleBytes) / elapsed;
            sampleStarted = now;
            sampleBytes = value.BytesTransferred;
        }
        var fraction = value.TotalBytes > 0 ? (double)value.BytesTransferred / value.TotalBytes : 1;
        var stage = job.Snapshot().Stage;
        var progress = stage == "Downloading" ? fraction * 10 : 85 + fraction * 14;
        job.Update(stage, progress, activity: new()
        {
            BytesTransferred = value.BytesTransferred,
            TotalBytes = value.TotalBytes,
            BytesPerSecond = bytesPerSecond,
            EstimatedSecondsRemaining = bytesPerSecond is > 0 ? Math.Max(0, value.TotalBytes - value.BytesTransferred) / bytesPerSecond.Value : null,
            CurrentFile = value.CurrentFile,
            FilesCompleted = value.FilesCompleted,
            FilesTotal = value.FilesTotal
        });
        lastPublished = now;
    }

    private void Write(WorkerJobProgress snapshot, string message, string level)
    {
        log(new()
        {
            Level = level,
            Message = message,
            JobId = job.Assignment.JobId,
            Attempt = job.Assignment.Attempt,
            Stage = snapshot.Stage,
            Progress = snapshot.Progress
        });
    }

    private static string Describe(WorkerJobProgress job, double elapsed)
    {
        var activity = job.Activity;
        if (activity?.BytesTransferred is { } completed && activity.TotalBytes is { } total)
        {
            var percent = total > 0 ? completed * 100d / total : 100;
            var speed = activity.BytesPerSecond is { } rate ? $", {Bytes(rate)}/s" : "";
            var eta = activity.EstimatedSecondsRemaining is { } remaining ? FormattableString.Invariant($", ETA {remaining:0}s") : "";
            var files = activity.FilesTotal is > 1 ? $", files {activity.FilesCompleted}/{activity.FilesTotal}" : "";
            return FormattableString.Invariant($"{job.Stage}: {percent:0.0}% ({Bytes(completed)} / {Bytes(total)}){speed}{eta}{files}");
        }
        if (activity?.ProcessedDurationMs is { } processed && activity.TotalDurationMs is { } duration)
        {
            var fps = activity.FramesPerSecond is { } frames ? FormattableString.Invariant($", {frames:0.0} fps") : "";
            var speed = activity.Speed is { } multiple ? FormattableString.Invariant($", {multiple:0.00}x") : "";
            var eta = activity.EstimatedSecondsRemaining is { } remaining ? FormattableString.Invariant($", ETA {remaining:0}s") : "";
            return FormattableString.Invariant($"Encoding: {processed / 1000:0.0}s / {duration / 1000:0.0}s{fps}{speed}{eta}");
        }
        return FormattableString.Invariant($"{job.Stage}: active for {elapsed:0}s");
    }

    private static string Bytes(double value)
    {
        if (value >= 1024 * 1024 * 1024)
        {
            return (value / (1024 * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " GiB";
        }
        if (value >= 1024 * 1024)
        {
            return (value / (1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " MiB";
        }
        if (value >= 1024)
        {
            return (value / 1024).ToString("0.00", CultureInfo.InvariantCulture) + " KiB";
        }
        return value.ToString("0", CultureInfo.InvariantCulture) + " B";
    }
}
