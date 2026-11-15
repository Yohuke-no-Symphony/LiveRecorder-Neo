using System.Diagnostics;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Scheduling;

internal sealed class WorkerRunningJob : IDisposable
{
    private readonly Lock sync = new();
    private long deadline;
    private bool disposed;
    private readonly WorkerJobProgress progress;
    internal TranscodeAssignment Assignment { get; }
    internal CancellationTokenSource Cancellation { get; }
    internal Task Task { get; set; } = Task.CompletedTask;

    internal WorkerRunningJob(TranscodeAssignment assignment, CancellationToken cancellationToken)
    {
        Assignment = assignment;
        Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        progress = new() { JobId = assignment.JobId, Attempt = assignment.Attempt, LeaseToken = assignment.LeaseToken };
        Renew(assignment.LeaseSeconds);
    }

    internal void Renew(int seconds) => Interlocked.Exchange(ref deadline,
        Stopwatch.GetTimestamp() + (long)(Math.Max(1, seconds - 5) * (double)Stopwatch.Frequency));

    internal void CheckLease()
    {
        if (Stopwatch.GetTimestamp() >= Interlocked.Read(ref deadline))
        {
            Cancel();
        }
    }

    internal void Update(string stage, double value, string? encoder = null)
    {
        lock (sync)
        {
            progress.Stage = stage;
            progress.Progress = Math.Clamp(value, 0, 99.9);
            progress.Encoder = encoder ?? progress.Encoder;
        }
    }

    internal WorkerJobProgress Snapshot()
    {
        lock (sync)
        {
            return progress with { };
        }
    }

    internal void Cancel()
    {
        lock (sync)
        {
            if (!disposed)
            {
                Cancellation.Cancel();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            Cancellation.Dispose();
        }
    }
}
