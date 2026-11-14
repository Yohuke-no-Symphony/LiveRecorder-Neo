using System.Text.Json.Nodes;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Analytics;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证在线人数的不规则采样、有效时长、断连及投影基线。</summary>
public sealed class AnalyticsOnlineTests
{
    [Fact]
    public void AverageUsesObservedTimeInsteadOfSampleCount()
    {
        var accumulator = new OnlineAnalyticsAccumulator();
        accumulator.Add(Online(0, 100));
        accumulator.Add(Online(50000, 200));

        var report = accumulator.Build(0, 60000);

        Assert.Equal(200, report.Peak);
        Assert.Equal(116.6666666667, report.Average!.Value, 8);
        Assert.Equal(60000, report.ObservedMs);
        Assert.Equal(1, report.CoverageRate);
    }

    [Fact]
    public void RepeatedValuesRenewCoverageWithoutBridgingExpiredSamples()
    {
        var accumulator = new OnlineAnalyticsAccumulator();
        accumulator.Add(Online(0, 100));
        accumulator.Add(Online(50000, 100));
        accumulator.Add(Online(180000, 200));

        var report = accumulator.Build(0, 300000);

        Assert.Equal(170000, report.ObservedMs);
        Assert.Equal(2, report.Intervals.Count);
        Assert.Equal((0d, 110000d), (report.Intervals[0].FromMs, report.Intervals[0].ToMs));
        Assert.Equal((180000d, 240000d), (report.Intervals[1].FromMs, report.Intervals[1].ToMs));
        Assert.Equal(170000d / 300000, report.CoverageRate, 10);
    }

    [Theory]
    [InlineData("RECORDER_DISCONNECTED", null)]
    [InlineData("", "RECORDING_EVENT_CONNECTION_LOST")]
    public void ConnectionLossStopsCoverageUntilFreshSample(string command, string? textCode)
    {
        var accumulator = new OnlineAnalyticsAccumulator();
        accumulator.Add(Online(0, 100));
        accumulator.Add(new() { Kind = "Gap", Command = command, TextCode = textCode, OffsetMs = 10000 });
        accumulator.Add(new() { Kind = "Recovery", Command = "RECORDER_RECOVERED", OffsetMs = 20000 });
        accumulator.Add(Online(30000, 200));

        var report = accumulator.Build(0, 60000);

        Assert.Equal(40000, report.ObservedMs);
        Assert.Equal(175, report.Average);
        Assert.Equal(10000, report.Intervals[0].ToMs);
        Assert.Equal(30000, report.Intervals[1].FromMs);
    }

    [Fact]
    public void HistoryAndRedPocketFailuresDoNotInterruptOnlineCoverage()
    {
        var accumulator = new OnlineAnalyticsAccumulator();
        accumulator.Add(Online(0, 100));
        accumulator.Add(new() { Kind = "Gap", Command = "RECORDER_HISTORY_FAILED", OffsetMs = 10000 });
        accumulator.Add(new() { Kind = "Gap", Command = "RECORDER_RED_POCKET_FAILED", OffsetMs = 20000 });

        Assert.Equal(60000, accumulator.Build(0, 60000).ObservedMs);
    }

    [Fact]
    public void CropUsesUnexpiredPrefixAndProjectsAbsoluteOffsets()
    {
        var accumulator = new OnlineAnalyticsAccumulator();
        accumulator.Add(Online(1000, 100));
        accumulator.Add(Online(50000, 200));

        var report = accumulator.Build(30000, 70000, -30000);

        Assert.Equal(150, report.Average);
        Assert.Equal(40000, report.ObservedMs);
        Assert.Equal((0d, 20000d), (report.Intervals[0].FromMs, report.Intervals[0].ToMs));
        Assert.Equal((20000d, 40000d), (report.Intervals[1].FromMs, report.Intervals[1].ToMs));
    }

    [Fact]
    public void MissingOnlineFieldDoesNotUseRankOrWatchedCountsAndZeroIsReal()
    {
        var accumulator = new OnlineAnalyticsAccumulator();
        accumulator.Add(new() { Kind = "Raw", Command = "ONLINE_RANK_COUNT", OffsetMs = 0, RawJson = """{"data":{"count":999}}""" });
        accumulator.Add(new() { Kind = "Raw", Command = "WATCHED_CHANGE", OffsetMs = 1000, RawJson = """{"data":{"num":999}}""" });
        accumulator.Add(new() { Kind = "Raw", Command = "ONLINE_RANK_COUNT", OffsetMs = 2000, RawJson = """{"data":{"online_count":-1}}""" });
        accumulator.Add(new() { Kind = "Raw", Command = "ONLINE_RANK_COUNT", OffsetMs = 3000, RawJson = """{"data":{"online_count":1.5}}""" });
        accumulator.Add(new() { Kind = "Raw", Command = "ONLINE_RANK_COUNT", OffsetMs = 4000, RawJson = "{broken" });
        var missing = accumulator.Build(0, 10000);
        Assert.Null(missing.Peak);
        Assert.Null(missing.Average);
        Assert.Equal(0, missing.ObservedMs);

        accumulator.Add(Online(5000, 0));
        var zero = accumulator.Build(0, 10000);
        Assert.Equal(0, zero.Peak);
        Assert.Equal(0, zero.Average);
        Assert.Equal(5000, zero.ObservedMs);
    }

    [Fact]
    public void ReorderedSamplesUseSequenceAtTheSameTimeAndExcludeRightBoundary()
    {
        var accumulator = new OnlineAnalyticsAccumulator();
        accumulator.Add(Online(50000, 999, 3));
        accumulator.Add(Online(10000, 100, 2));
        accumulator.Add(Online(10000, 500, 1));
        accumulator.Add(Online(0, 50));

        var report = accumulator.Build(0, 50000);

        Assert.Equal(100, report.Peak);
        Assert.Equal(90, report.Average);
    }

    [Fact]
    public void ExpiredPrefixAndEmptyRangeStayUnknown()
    {
        var accumulator = new OnlineAnalyticsAccumulator();
        accumulator.Add(Online(0, 100));

        Assert.Null(accumulator.Build(60000, 120000).Peak);
        Assert.Null(accumulator.Build(5000, 5000).Average);
        Assert.Empty(accumulator.Build(5000, 5000).Intervals);
    }

    private static RecordedEventEnvelope Online(double offsetMs, long count, long sequence = 0) => new()
    {
        Kind = "Raw",
        Command = "ONLINE_RANK_COUNT",
        OffsetMs = offsetMs,
        Sequence = sequence,
        Payload = new JsonObject { ["data"] = new JsonObject { ["online_count"] = count } }
    };
}
