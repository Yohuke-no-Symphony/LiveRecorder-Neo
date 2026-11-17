using Yohuke.LiveRecorder.Recording.Tests;
using Yohuke.LiveRecorder.Recording.Worker.Capture;
using Xunit;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

/// <summary>验证存储背压和进程恢复不会绕过场次截止时间。</summary>
public sealed class RecordingRoomDeadlineTests
{
    /// <summary>缓存满时仍然结束已达到无媒体上限的场次。</summary>
    [Fact]
    public async Task StorageFullCannotPreventInactivityExpiry()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        var assignment = fixture.Assignment with { RecordDanmu = false, RecordingTimeoutMinutes = 1 };
        spool.SaveAssignment(assignment, fixture.DirectoryPath);
        var clock = new ManualRecordingTimeProvider();
        var recorder = new RecordingRetryTestRecorder();
        var configuration = fixture.Configuration;
        using var cancellation = new CancellationTokenSource();
        using var runner = new RecordingRoomRunner(assignment, spool, new RecordingTestCaptureSource(), _ => recorder,
            () => configuration, clock, _ => { }, cancellation.Token);
        runner.Start();
        try
        {
            await recorder.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            configuration = configuration with { MaxCacheBytes = 0 };
            recorder.EndFirst.TrySetResult();
            await WaitAsync(() => runner.Snapshot().State == "Retrying");
            clock.Advance(TimeSpan.FromSeconds(2));
            await WaitAsync(() => runner.Snapshot().State == "StorageFull");
            clock.Advance(TimeSpan.FromMinutes(1));

            await WaitAsync(() => spool.Sessions().Any(value => value.State.Descriptor.IsClosed));
            Assert.False(recorder.SecondStarted.Task.IsCompleted);
        }
        finally
        {
            await cancellation.CancelAsync();
            await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>恢复时已到期的场次在发起新媒体连接之前封口。</summary>
    [Fact]
    public async Task RecoveredSessionExpiresBeforeResolvingAnotherStream()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        var assignment = fixture.Assignment with { RecordDanmu = false, RecordingTimeoutMinutes = 1 };
        spool.SaveAssignment(assignment, fixture.DirectoryPath);
        var session = fixture.Session(spool);
        var clock = new ManualRecordingTimeProvider();
        spool.UpdateSession(session.DirectoryPath, value => value.LastMediaReceivedAtUtc = clock.GetUtcNow().UtcDateTime.AddMinutes(-2));
        var recorder = new RecordingSilentRecorder();
        using var cancellation = new CancellationTokenSource();
        using var runner = new RecordingRoomRunner(assignment, spool, new RecordingTestCaptureSource(), _ => recorder,
            () => fixture.Configuration, clock, _ => { }, cancellation.Token);
        runner.Start();
        try
        {
            await WaitAsync(() => spool.Sessions().Any(value => value.State.Descriptor.IsClosed));
            Assert.False(recorder.Started.Task.IsCompleted);
        }
        finally
        {
            await cancellation.CancelAsync();
            await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
