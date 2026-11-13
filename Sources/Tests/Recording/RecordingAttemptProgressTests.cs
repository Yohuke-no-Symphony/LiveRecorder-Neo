using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Xunit;

namespace Yohuke.LiveRecorder.Recording.Tests;

/// <summary>验证有效媒体识别与场次延迟激活的进度交接。</summary>
public sealed class RecordingAttemptProgressTests
{
    /// <summary>开文件、头信息和关闭通知都不能伪装成有效媒体。</summary>
    [Fact]
    public void HeadersAndLifecycleNotificationsDoNotActivateOrExtendRecording()
    {
        var clock = new ManualRecordingTimeProvider();
        using var monitor = new RecordingInactivityMonitor(TimeSpan.FromMinutes(1), clock);
        var progress = new RecordingAttemptProgress(monitor);
        clock.Advance(TimeSpan.FromSeconds(40));
        progress.Report(Update("Opened", 0, 0));
        progress.Report(Update("Committed", 300, 0));
        progress.Report(Update("Closed", 1000, 100));

        clock.Advance(TimeSpan.FromSeconds(20));

        Assert.False(progress.HasMedia);
        Assert.False(progress.MediaReceived.IsCompletedSuccessfully);
        Assert.True(monitor.IsExpired);
    }

    /// <summary>延迟激活保留第一次已提交媒体，并继续转发同一连接的后续帧。</summary>
    [Fact]
    public void ActivationRetainsTheFirstMediaAndForwardsFurtherProgress()
    {
        var clock = new ManualRecordingTimeProvider();
        using var monitor = new RecordingInactivityMonitor(TimeSpan.FromMinutes(1), clock);
        var progress = new RecordingAttemptProgress(monitor);
        var sink = new RecordingProgressCollector();
        progress.Report(Update("Opened", 0, 0));
        progress.Report(Update("Committed", 300, 0));
        progress.Report(Update("Committed", 1000, 100));
        Assert.True(progress.HasMedia);
        Assert.True(progress.MediaReceived.IsCompletedSuccessfully);
        Assert.Empty(sink.Values);

        progress.Activate(sink);

        var firstMedia = Assert.Single(sink.Values, update => update is { SegmentId: "segment-a", Kind: "Committed", CommittedBytes: 1000 });
        Assert.Equal(100, firstMedia.DurationMs, 3);
        var count = sink.Values.Count;
        progress.Report(Update("Committed", 2000, 200));
        Assert.Equal(count + 1, sink.Values.Count);
        Assert.Equal(2000, sink.Values[^1].CommittedBytes);
        Assert.Equal(200, sink.Values[^1].DurationMs);
    }

    /// <summary>重复提交和关闭回调不延长最后一帧之后的等待时限。</summary>
    [Fact]
    public void DuplicateCommitsAndCloseNotificationsDoNotRestartTheDeadline()
    {
        var clock = new ManualRecordingTimeProvider();
        using var monitor = new RecordingInactivityMonitor(TimeSpan.FromMinutes(1), clock);
        var progress = new RecordingAttemptProgress(monitor);
        progress.Report(Update("Committed", 1000, 100));
        clock.Advance(TimeSpan.FromSeconds(55));
        progress.Report(Update("Committed", 1000, 100));
        progress.Report(Update("Closed", 2000, 200));

        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.True(monitor.IsExpired);
    }

    /// <summary>只有字节或时长单项增长时不能判定媒体已经恢复。</summary>
    [Theory]
    [InlineData(1000, 200)]
    [InlineData(2000, 100)]
    public void MediaRecoveryRequiresBothCommittedBytesAndDurationToAdvance(long bytes, double duration)
    {
        var clock = new ManualRecordingTimeProvider();
        using var monitor = new RecordingInactivityMonitor(TimeSpan.FromMinutes(1), clock);
        var progress = new RecordingAttemptProgress(monitor);
        progress.Report(Update("Committed", 1000, 100));
        clock.Advance(TimeSpan.FromSeconds(55));
        progress.Report(Update("Committed", bytes, duration));

        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.True(monitor.IsExpired);
    }

    /// <summary>新分段的媒体时长独立比较，恢复后的短首帧也能延长时限。</summary>
    [Fact]
    public void NewSegmentActivityIsComparedAgainstItsOwnProgress()
    {
        var clock = new ManualRecordingTimeProvider();
        using var monitor = new RecordingInactivityMonitor(TimeSpan.FromMinutes(1), clock);
        var progress = new RecordingAttemptProgress(monitor);
        progress.Report(Update("Committed", 2000, 1000));
        clock.Advance(TimeSpan.FromSeconds(50));
        progress.Report(Update("Committed", 500, 20, "segment-b"));
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(monitor.IsExpired);

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(monitor.IsExpired);
    }

    /// <summary>激活前已关闭分段的最终提交边界不会在进度合并中遗失。</summary>
    [Fact]
    public void ActivationPreservesClosedSegmentsAndTheirCommittedBoundary()
    {
        var clock = new ManualRecordingTimeProvider();
        using var monitor = new RecordingInactivityMonitor(TimeSpan.FromMinutes(1), clock);
        var progress = new RecordingAttemptProgress(monitor);
        var sink = new RecordingProgressCollector();
        progress.Report(Update("Opened", 0, 0));
        progress.Report(Update("Committed", 1000, 100));
        progress.Report(Update("Closed", 1000, 100));
        progress.Report(Update("Committed", 500, 20, "segment-b"));

        progress.Activate(sink);

        var closed = Assert.Single(sink.Values, update => update is { SegmentId: "segment-a", Kind: "Closed", CommittedBytes: 1000 });
        Assert.Equal(100, closed.DurationMs, 3);
        Assert.Contains(sink.Values, update => update.SegmentId == "segment-b" && update.CommittedBytes == 500);
    }

    private static MediaRecordingUpdate Update(string kind, long bytes, double duration, string segmentId = "segment-a")
    {
        return new()
        {
            Kind = kind,
            SegmentId = segmentId,
            RelativePath = segmentId + ".flv",
            IndexRelativePath = segmentId + ".index.jsonl",
            CommittedBytes = bytes,
            DurationMs = duration
        };
    }
}
