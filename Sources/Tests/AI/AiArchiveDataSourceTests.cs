using System.Text.Json;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Xunit;

namespace Yohuke.LiveRecorder.AI.Tests;

public sealed class AiArchiveDataSourceTests
{
    [Fact]
    public async Task MetricsReuseTimeWeightedOnlineCoverageAndDisconnectExpiryRules()
    {
        using var fixture = new AiTestWorkspace();
        var detail = AiTestWorkspace.Session();
        await fixture.WriteAsync("source", Online(0, 100), new() { Kind = "Danmu", Text = "T:歌曲", OffsetMs = 5000 },
            new() { Kind = "Gap", Command = "RECORDER_DISCONNECTED", OffsetMs = 10000 }, Online(30000, 200),
            new() { Kind = "SuperChat", Text = "感谢", OffsetMs = 50000, Amount = 30 }, Online(100000, 300));
        var snapshot = await fixture.Data.CreateAsync(detail, fixture.Settings, CancellationToken.None);
        var canonical = await fixture.Analytics.GetAsync(detail.Session, fixture.Paths.RecordsDirectory, detail.DurationMs, "^T:");
        Assert.Equal(90000, snapshot.OnlineObservedMs);
        Assert.Equal(211.111111111, snapshot.OnlineAverage!.Value, 7);
        Assert.Equal(300, snapshot.OnlinePeak);
        Assert.Equal(0.75, snapshot.OnlineCoverageRate);
        Assert.Equal(canonical.Online.Average, snapshot.OnlineAverage);
        Assert.Equal(canonical.Buckets.Select(item => (item.OnlineAverage, item.OnlinePeak, item.OnlineObservedMs)),
            snapshot.Buckets.Select(item => (item.OnlineAverage, item.OnlinePeak, item.OnlineObservedMs)));
        Assert.Equal(1, snapshot.Buckets.Sum(item => item.TranslationCount));
        Assert.Equal(1, snapshot.Buckets.Sum(item => item.SuperChatCount));
    }

    [Fact]
    public async Task RecordedDurationExcludesGapsBetweenGroupedSources()
    {
        using var fixture = new AiTestWorkspace();
        var detail = AiTestWorkspace.Session(14400000);
        detail.Session.EventSources =
        [
            new() { DirectoryPath = "first", FromMs = 0, ToMs = 3600000, StartOffsetMs = 0 },
            new() { DirectoryPath = "second", FromMs = 0, ToMs = 3600000, StartOffsetMs = 10800000 }
        ];
        await fixture.WriteAsync("first", new RecordedEventEnvelope { Kind = "Danmu", Text = "T:前半", OffsetMs = 1000 });
        await fixture.WriteAsync("second", new RecordedEventEnvelope { Kind = "Danmu", Text = "T:后半", OffsetMs = 1000 });
        var snapshot = await fixture.Data.CreateAsync(detail, fixture.Settings, CancellationToken.None);
        Assert.Equal(7200000, snapshot.RecordedDurationMs);
        Assert.Equal(14400000, snapshot.DurationMs);
        Assert.Equal(2, snapshot.EvidenceCount);
        var page = await fixture.Data.ReadAsync(snapshot, 0, snapshot.DurationMs, "All", null, null, 40, CancellationToken.None);
        Assert.Equal(new double[] { 1000, 10801000 }, page.Items.Select(item => item.OffsetMs));
    }

    [Fact]
    public async Task EvidenceIsImmutableSanitizedAndBoundedWithAContinuableCursor()
    {
        using var fixture = new AiTestWorkspace();
        var detail = AiTestWorkspace.Session();
        var events = Enumerable.Range(0, 45).Select(index => new RecordedEventEnvelope
        {
            Kind = "Danmu", OffsetMs = 1000 + index, Text = "T:" + index + new string('文', 600),
            UserName = "private-name", RawJson = "private-raw-payload"
        }).ToArray();
        await fixture.WriteAsync("source", events);
        var snapshot = await fixture.Data.CreateAsync(detail, fixture.Settings, CancellationToken.None);
        var path = Path.Combine(fixture.Paths.DataDirectory, snapshot.EvidenceRelativePath);
        var content = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("private-name", content);
        Assert.DoesNotContain("private-raw-payload", content);
        var first = await fixture.Data.ReadAsync(snapshot, 0, 120000, "Translation", null, null, 40, CancellationToken.None);
        Assert.True(first.Truncated);
        Assert.InRange(first.Items.Sum(item => item.Text.Length), 1, 4000);
        Assert.All(first.Items, item => Assert.InRange(item.Text.Length, 1, 400));
        var second = await fixture.Data.ReadAsync(snapshot, 0, 120000, "Translation", null, first.NextCursor, 40, CancellationToken.None);
        Assert.Empty(first.Items.Select(item => item.Id).Intersect(second.Items.Select(item => item.Id)));
        await File.AppendAllTextAsync(Path.Combine(fixture.Paths.RecordsDirectory, "source", "弹幕", "事件.jsonl"),
            JsonSerializer.Serialize(new RecordedEventEnvelope { Sequence = 46, EventId = "late", Kind = "Danmu", Text = "T:later", OffsetMs = 2000 }, JsonFiles.Options) + "\n");
        Assert.NotEqual(snapshot.SourceFingerprint, fixture.Data.Fingerprint(detail, fixture.Settings));
        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task TranslationTimeoutReturnsTheSharedErrorAndDeletesPartialSnapshot()
    {
        using var fixture = new AiTestWorkspace();
        fixture.Settings.TranslationDanmuPattern = "^(a+)+$";
        await fixture.WriteAsync("source", new RecordedEventEnvelope { Kind = "Danmu", Text = new string('a', 50000) + "!", OffsetMs = 1000 });
        var error = await Assert.ThrowsAsync<InfrastructureException>(() => fixture.Data.CreateAsync(AiTestWorkspace.Session(), fixture.Settings, CancellationToken.None));
        Assert.Equal("TRANSLATION_DANMU_PATTERN_TIMEOUT", error.Code);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Paths.DataDirectory, "ai", "snapshots")));
    }

    private static RecordedEventEnvelope Online(double offset, long count) => new()
    {
        Kind = "Raw", Command = "ONLINE_RANK_COUNT", OffsetMs = offset, RawJson = JsonSerializer.Serialize(new { data = new { online_count = count } })
    };
}
