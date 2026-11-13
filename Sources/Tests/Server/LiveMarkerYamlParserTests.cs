using System.Globalization;
using System.Text;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Library.LiveMarkers;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Infrastructure.Library.LiveMarkers;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证原生记录兼容、独立时刻、来源时区和不可信上传边界。</summary>
public sealed class LiveMarkerYamlParserTests
{
    /// <summary>旧文件的实际时间不从开始时间与直播时长重新推导。</summary>
    [Fact]
    public void OriginalTimesRemainIndependentWithTheirExplicitOffsets()
    {
        var document = Parse("2026-10-20T18:00:00+08:00", "2026-10-20T19:55:00.1234567-04:00", "00:04:00");
        var entry = Assert.Single(document.Entries);
        Assert.Equal(TimeSpan.FromMinutes(4), entry.LiveTime);
        Assert.Equal(TimeSpan.FromHours(-4), entry.RealDateTime.Offset);
        Assert.Equal(1234567, entry.RealDateTime.Ticks % TimeSpan.TicksPerSecond);
        Assert.NotEqual(document.StartTime + entry.LiveTime, entry.RealDateTime);
        Assert.Equal("quoted \"日本語\"\nnext", entry.Message);
    }

    /// <summary>累计小时和旧天数格式保持到 tick 的精度且不在 24 小时回绕。</summary>
    [Theory]
    [InlineData("1.01:02:03.1234567")]
    [InlineData("25:02:03.1234567")]
    public void LegacyAndTotalHourDurationsAreEquivalent(string duration)
    {
        var document = Parse("2026-10-20T18:00:00Z", "2026-10-20T18:04:00Z", duration);
        Assert.Equal(
            TimeSpan.FromHours(25) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3) + TimeSpan.FromTicks(1234567),
            Assert.Single(document.Entries).LiveTime);
    }

    /// <summary>旧无偏移日期只按上传时所选来源时区解释，显式偏移不受影响。</summary>
    [Fact]
    public void LegacyDatesUseTheSelectedSourceTimeZone()
    {
        var document = Parse("2026-10-20T18:00:00", "2026-10-20T18:04:00+09:00", "00:04:00", "Asia/Shanghai");
        Assert.Equal(DateTimeOffset.Parse("2026-10-20T10:00:00Z", CultureInfo.InvariantCulture), document.StartTime);
        Assert.Equal(TimeSpan.FromHours(8), document.StartTime.Offset);
        Assert.Equal(TimeSpan.FromHours(9), document.Entries[0].RealDateTime.Offset);
    }

    /// <summary>无偏移的夏令时歧义与不存在时刻要求来源文件补充明确偏移。</summary>
    [Theory]
    [InlineData("2026-11-01T01:30:00")]
    [InlineData("2026-03-08T02:30:00")]
    public void AmbiguousOrMissingDaylightSavingTimesAreRejected(string timestamp)
    {
        var error = Assert.Throws<InfrastructureException>(() =>
            Parse(timestamp, "2026-10-20T18:04:00Z", "00:04:00", "America/New_York"));
        Assert.Equal(MessageCode.LIVE_MARKER_TIME_ZONE_AMBIGUOUS.ToString(), error.Code);
    }

    /// <summary>畸形、缺字段、未知颜色、别名及多文档不能生成猜测记录。</summary>
    [Theory]
    [InlineData("StartTime: 2026-10-20T18:00:00Z\nMarker: []\nUnexpected: value")]
    [InlineData("StartTime: 2026-10-20T18:00:00Z\nMarker: {}")]
    [InlineData(
        "StartTime: 2026-10-20T18:00:00Z\nMarker: [{RealDateTime: 2026-10-20T18:04:00Z, LiveTime: -00:00:01, Message: x, MarkerColor: Red}]")]
    [InlineData(
        "StartTime: 2026-10-20T18:00:00Z\nMarker: [{RealDateTime: 2026-10-20T18:04:00Z, LiveTime: 00:04:00, Message: x, MarkerColor: 1}]")]
    [InlineData(
        "StartTime: &t 2026-10-20T18:00:00Z\nMarker: [{RealDateTime: *t, LiveTime: 00:04:00, Message: x, MarkerColor: Red}]")]
    [InlineData("StartTime: 2026-10-20T18:00:00Z\nStartTime: 2026-10-20T18:00:00Z\nMarker: []")]
    [InlineData("StartTime: 2026-10-20T18:00:00Z\nMarker: []\n---\nStartTime: 2026-10-20T18:00:00Z\nMarker: []")]
    public void InvalidYamlIsRejected(string yaml)
    {
        Assert.Throws<InfrastructureException>(() =>
            new LiveMarkerYamlParser().Parse(Encoding.UTF8.GetBytes(yaml), TimeZoneInfo.Utc));
    }

    /// <summary>大小和条目限制在生成持久化记录之前生效。</summary>
    [Fact]
    public void UploadByteAndEntryLimitsAreEnforced()
    {
        var parser = new LiveMarkerYamlParser();
        var tooLarge = Assert.Throws<InfrastructureException>(() =>
            parser.Parse(new byte[LiveMarkerLimits.MAXIMUM_BYTES + 1], TimeZoneInfo.Utc));
        Assert.Equal(413, tooLarge.StatusCode);
        var entry = "- { RealDateTime: 2026-10-20T18:04:00Z, LiveTime: 00:04:00, Message: x, MarkerColor: Red }\n";
        var yaml = "StartTime: 2026-10-20T18:00:00Z\nMarker:\n" +
                   string.Concat(Enumerable.Repeat(entry, LiveMarkerLimits.MAXIMUM_ENTRIES + 1));
        var tooMany =
            Assert.Throws<InfrastructureException>(() => parser.Parse(Encoding.UTF8.GetBytes(yaml), TimeZoneInfo.Utc));
        Assert.Equal(MessageCode.LIVE_MARKER_TOO_MANY_ENTRIES.ToString(), tooMany.Code);
    }

    /// <summary>整体校准保留正负值，但不能将实际时间推至可表示范围之外。</summary>
    [Fact]
    public void AlignmentRejectsOverflowWithoutChangingTheOriginalDocument()
    {
        var document = Parse("2026-10-20T18:00:00Z", "2026-10-20T18:04:00Z", "00:04:00");
        LiveMarkerYamlParser.ValidateAlignment(document, 1500);
        LiveMarkerYamlParser.ValidateAlignment(document, -1500);
        Assert.Throws<InfrastructureException>(() => LiveMarkerYamlParser.ValidateAlignment(document, long.MaxValue));
        Assert.Throws<InfrastructureException>(() =>
            LiveMarkerYamlParser.ValidateAlignment(document, -LiveMarkerLimits.MAXIMUM_CALIBRATION_MS));
        Assert.Equal(DateTimeOffset.Parse("2026-10-20T18:04:00Z", CultureInfo.InvariantCulture),
            document.Entries[0].RealDateTime);
    }

    private static Yohuke.LiveRecorder.Domain.LiveMarkers.LiveMarkerDocument Parse(string start, string real,
        string duration, string zone = "UTC")
    {
        var yaml =
            $"StartTime: {start}\nMarker:\n- RealDateTime: {real}\n  LiveTime: {duration}\n  Message: \"quoted \\\"日本語\\\"\\nnext\"\n  MarkerColor: Magenta\n";
        return new LiveMarkerYamlParser().Parse(Encoding.UTF8.GetBytes(yaml),
            LiveMarkerYamlParser.ResolveTimeZone(zone));
    }
}
