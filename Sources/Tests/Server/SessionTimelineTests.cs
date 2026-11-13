using Xunit;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Tests;

public sealed class SessionTimelineTests
{
    /// <summary>已结束场次以最后媒体为播放终点，原始结束时间仍保留。</summary>
    [Fact]
    public void CompletedSessionEndsPlaybackAtTheLastMedia()
    {
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var session = new BroadcastSession { StartedAtUtc = start, EndedAtUtc = start.AddMinutes(5) };
        var segment = new RecordingSegment { StartOffsetMs = 0, DurationMs = 7000, IsClosed = true };

        var detail = SessionTimeline.Build(session, [segment], start.AddHours(1));

        Assert.Equal(7000, detail.DurationMs);
        Assert.Empty(detail.Gaps);
        Assert.Same(segment, Assert.Single(detail.Segments));
        Assert.Equal(start.AddMinutes(5), session.EndedAtUtc);
    }

    /// <summary>零时长尾段不延长播放或产生空档，原分段清单仍完整。</summary>
    [Fact]
    public void CompletedCoverageIgnoresEmptySegmentsAndPreservesInternalGaps()
    {
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var session = new BroadcastSession { StartedAtUtc = start, EndedAtUtc = start.AddSeconds(30) };
        var empty = new RecordingSegment { StartOffsetMs = 20000, DurationMs = 0, IsClosed = true };

        var detail = SessionTimeline.Build(session,
        [
            empty,
            new RecordingSegment { StartOffsetMs = 5000, DurationMs = 1000, IsClosed = true },
            new RecordingSegment { StartOffsetMs = 1000, DurationMs = 2000, IsClosed = true }
        ], start);

        Assert.Equal(6000, detail.DurationMs);
        Assert.Equal(3, detail.Segments.Count);
        Assert.Same(empty, detail.Segments[^1]);
        Assert.Equal(new[] { (0d, 1000d), (3000d, 5000d) }, detail.Gaps.Select(gap => (gap.StartMs, gap.EndMs)));
    }

    /// <summary>没有有效媒体的已结束场次不暴露仅含中断的播放区间。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletedSessionWithoutMediaHasZeroPlaybackDuration(bool hasEmptySegment)
    {
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var segments = hasEmptySegment
            ? new[] { new RecordingSegment { StartOffsetMs = 10000, DurationMs = 0, IsClosed = true } }
            : [];

        var detail = SessionTimeline.Build(new() { StartedAtUtc = start, EndedAtUtc = start.AddMinutes(1) }, segments, start);

        Assert.Equal(0, detail.DurationMs);
        Assert.Empty(detail.Gaps);
        Assert.Equal(segments.Length, detail.Segments.Count);
    }

    /// <summary>有效媒体晚于保存的结束时间时仍完整保留媒体。</summary>
    [Fact]
    public void CompletedSessionRetainsMediaBeyondItsStoredEndTime()
    {
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var detail = SessionTimeline.Build(new() { StartedAtUtc = start, EndedAtUtc = start.AddSeconds(10) },
            [new RecordingSegment { StartOffsetMs = 0, DurationMs = 12000, IsClosed = true }], start);

        Assert.Equal(12000, detail.DurationMs);
        Assert.Empty(detail.Gaps);
    }

    [Fact]
    public void RealTimeGapsRemainBetweenSegments()
    {
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var session = new BroadcastSession { StartedAtUtc = start, EndedAtUtc = start.AddMinutes(5) };
        var detail = SessionTimeline.Build(session,
        [
            new RecordingSegment { StartOffsetMs = 0, DurationMs = 60000 },
            new RecordingSegment { StartOffsetMs = 180000, DurationMs = 120000 }
        ], start.AddHours(1));
        Assert.Equal(300000, detail.DurationMs);
        var gap = Assert.Single(detail.Gaps);
        Assert.Equal(60000, gap.StartMs);
        Assert.Equal(180000, gap.EndMs);
    }

    [Fact]
    public void OverlappingSegmentsDoNotCreateFalseGaps()
    {
        var start = DateTime.UtcNow;
        var detail = SessionTimeline.Build(new BroadcastSession { StartedAtUtc = start, EndedAtUtc = start.AddSeconds(20) },
        [
            new RecordingSegment { StartOffsetMs = 0, DurationMs = 12000 },
            new RecordingSegment { StartOffsetMs = 10000, DurationMs = 10000 }
        ], start);
        Assert.Empty(detail.Gaps);
        Assert.Equal(20000, detail.DurationMs);
    }

    [Fact]
    public void ExportsPreserveGapDurationAndTrimSourceOffsets()
    {
        var segments = new List<RecordingSegment>
        {
            new() { RelativePath = "one.flv", StartOffsetMs = 0, DurationMs = 10000 },
            new() { RelativePath = "two.flv", StartOffsetMs = 20000, DurationMs = 10000 }
        };
        var inputs = SessionTimeline.ExportSpans(segments, 5000, 25000, path => path);
        Assert.Equal(20000, inputs.Sum(x => x.DurationMs));
        Assert.Equal(5000, inputs[0].SourceStartMs);
        Assert.True(inputs[1].IsGap);
        Assert.Equal(10000, inputs[1].DurationMs);
    }

    [Fact]
    public void ActiveWriterTailIsPendingRatherThanARecordingOutage()
    {
        var start = DateTime.UtcNow;
        var detail = SessionTimeline.Build(new BroadcastSession { StartedAtUtc = start },
            [new RecordingSegment { StartOffsetMs = 0, DurationMs = 7000, IsClosed = false }], start.AddSeconds(10));
        Assert.Equal(10000, detail.DurationMs);
        Assert.Empty(detail.Gaps);
    }

    [Fact]
    public void DisconnectedClosedWriterLeavesARealTimeGap()
    {
        var start = DateTime.UtcNow;
        var detail = SessionTimeline.Build(new BroadcastSession { StartedAtUtc = start },
            [new RecordingSegment { StartOffsetMs = 0, DurationMs = 7000, IsClosed = true }], start.AddSeconds(10));
        var gap = Assert.Single(detail.Gaps);
        Assert.Equal(7000, gap.StartMs);
        Assert.Equal(10000, gap.EndMs);
    }

    [Fact]
    public void FirstConnectionWaitIsNotReportedAsADisconnect()
    {
        var start = DateTime.UtcNow;
        var detail = SessionTimeline.Build(new BroadcastSession { StartedAtUtc = start }, [], start.AddSeconds(2));
        Assert.Empty(detail.Gaps);
    }
}
