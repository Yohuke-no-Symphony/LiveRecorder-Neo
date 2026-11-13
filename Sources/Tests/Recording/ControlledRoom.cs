namespace Yohuke.LiveRecorder.Recording.Tests;

internal sealed class ControlledRoom(long roomId)
{
    private string title = "初始直播标题";
    private long liveTimeTicks = DateTime.UtcNow.AddMinutes(-1).Ticks;
    private long readBytes;
    private int activeStreams;
    private int connections;
    private int activeSubscriptions;
    private int blockNextQuery;
    private int queries;
    private Exception? queryFailure;
    private Exception? streamFailure;
    private TaskCompletionSource? mediaResume;
    private int streamRequests;
    private int headerOnly;
    private long events;
    internal long RoomId { get; } = roomId;
    internal string Title { get => Volatile.Read(ref title); set => Volatile.Write(ref title, value); }
    internal DateTime LiveStartedAtUtc { get => new(Interlocked.Read(ref liveTimeTicks), DateTimeKind.Utc); set => Interlocked.Exchange(ref liveTimeTicks, value.Ticks); }
    internal bool IsLive { get; set; } = true;
    internal Exception? QueryFailure { get => Volatile.Read(ref queryFailure); set => Volatile.Write(ref queryFailure, value); }
    internal Exception? StreamFailure { get => Volatile.Read(ref streamFailure); set => Volatile.Write(ref streamFailure, value); }
    internal bool HeaderOnly { get => Volatile.Read(ref headerOnly) != 0; set => Volatile.Write(ref headerOnly, value ? 1 : 0); }
    internal int StreamRequests => Volatile.Read(ref streamRequests);
    internal TaskCompletionSource MediaPaused { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int? DisconnectAfterMs { get; set; }
    internal double PaceScale { get; set; } = 1;
    internal long ReadBytes => Interlocked.Read(ref readBytes);
    internal int ActiveStreams => Volatile.Read(ref activeStreams);
    internal int Connections => Volatile.Read(ref connections);
    internal int ActiveSubscriptions => Volatile.Read(ref activeSubscriptions);
    internal int Queries => Volatile.Read(ref queries);
    internal TaskCompletionSource QueryEntered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ReleaseQuery { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void BlockNextQuery()
    {
        QueryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ReleaseQuery = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref blockNextQuery, 1);
    }

    internal async Task BeforeQueryAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref queries);
        if (Interlocked.Exchange(ref blockNextQuery, 0) == 1)
        {
            QueryEntered.TrySetResult();
            await ReleaseQuery.Task.WaitAsync(cancellationToken);
        }
    }

    internal void PauseMedia()
    {
        MediaPaused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref mediaResume, new(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    internal void ResumeMedia()
    {
        Interlocked.Exchange(ref mediaResume, null)?.TrySetResult();
    }

    internal async Task BeforeReadAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref mediaResume) is { } resume)
        {
            MediaPaused.TrySetResult();
            await resume.Task.WaitAsync(cancellationToken);
        }
        if (StreamFailure is { } failure)
        {
            throw failure;
        }
    }

    internal void RequestStream()
    {
        Interlocked.Increment(ref streamRequests);
    }

    internal void OpenStream()
    {
        Interlocked.Increment(ref activeStreams);
        Interlocked.Increment(ref connections);
    }
    internal void CloseStream() => Interlocked.Decrement(ref activeStreams);
    internal void Read(int count) => Interlocked.Add(ref readBytes, count);
    internal void OpenSubscription() => Interlocked.Increment(ref activeSubscriptions);
    internal void CloseSubscription() => Interlocked.Decrement(ref activeSubscriptions);
    internal long NextEvent() => Interlocked.Increment(ref events);
}
