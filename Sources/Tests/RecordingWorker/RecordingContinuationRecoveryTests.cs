using Yohuke.LiveRecorder.Recording.Contracts;
using Yohuke.LiveRecorder.Recording.Tests;
using Yohuke.LiveRecorder.Recording.Worker.Capture;
using Xunit;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

/// <summary>验证交接后尚未产生物理媒体时也持久保存逻辑场次等待。</summary>
public sealed class RecordingContinuationRecoveryTests
{
    /// <summary>没有媒体文件的接续等待跨进程恢复，原离线宽限不会重新起算。</summary>
    [Fact]
    public async Task OfflineContinuationKeepsItsDeadlineBeforeAnyMediaIsCaptured()
    {
        using var fixture = new RecordingWorkerFixture();
        using var spool = fixture.Spool();
        var clock = new ManualRecordingTimeProvider();
        var assignment = fixture.Assignment with
        {
            RecordDanmu = false, SessionGraceMinutes = 1,
            Continuation = new()
            {
                LogicalSessionId = "continued-broadcast", StartedAtUtc = clock.GetUtcNow().UtcDateTime,
                Lifecycle = new() { LastMediaReceivedAtUtc = clock.GetUtcNow().UtcDateTime }
            }
        };
        spool.SaveAssignment(assignment, fixture.DirectoryPath);
        var source = new RecordingTestCaptureSource { IsLive = false };
        using (var cancellation = new CancellationTokenSource())
        using (var runner = new RecordingRoomRunner(assignment, spool, source, _ => new RecordingSilentRecorder(),
            () => fixture.Configuration, clock, _ => { }, cancellation.Token))
        {
            runner.Start();
            try
            {
                await WaitAsync(() => runner.Snapshot().Continuation?.Lifecycle.OfflineSinceUtc is not null);
                Assert.NotNull(Assert.Single(spool.Assignments()).LastContinuation?.Lifecycle.OfflineSinceUtc);
                Assert.Empty(spool.Sessions());
            }
            finally
            {
                await cancellation.CancelAsync();
                await runner.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        clock.Advance(TimeSpan.FromMinutes(1));
        using var restartedCancellation = new CancellationTokenSource();
        using var restarted = new RecordingRoomRunner(assignment, spool, source, _ => new RecordingSilentRecorder(),
            () => fixture.Configuration, clock, _ => { }, restartedCancellation.Token);
        restarted.Start();
        try
        {
            await WaitAsync(() => restarted.Snapshot().Continuation?.Lifecycle.EndReason == RecordingSessionEndReason.OfflineGraceExpired);
            Assert.Empty(spool.Sessions());
        }
        finally
        {
            await restartedCancellation.CancelAsync();
            await restarted.Completion.WaitAsync(TimeSpan.FromSeconds(5));
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
