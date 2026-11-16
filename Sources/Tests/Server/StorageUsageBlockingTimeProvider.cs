namespace Yohuke.LiveRecorder.Tests;

internal sealed class StorageUsageBlockingTimeProvider : TimeProvider
{
    private int reads;
    internal TaskCompletionSource SnapshotCaptured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        if (Interlocked.Increment(ref reads) == 1)
        {
            SnapshotCaptured.TrySetResult();
            Resume.Task.GetAwaiter().GetResult();
        }
        return DateTimeOffset.UtcNow;
    }
}
