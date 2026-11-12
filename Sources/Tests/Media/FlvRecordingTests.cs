using System.Text.Json;
using Xunit;

namespace Yohuke.LiveRecorder.Media.Tests;

/// <summary>Exercises repaired recording and its real media index.</summary>
public sealed class FlvRecordingTests
{
    /// <summary>Unicode titles remain readable without exceeding filesystem component byte limits.</summary>
    [Fact]
    public async Task UnicodeTitleFitsFilesystemByteLimit()
    {
        using var fixture = new MediaFixture();
        var sample = await fixture.CreateSampleAsync(duration: 2);
        var title = string.Concat(Enumerable.Repeat("🐈直播测试标题/", 40));
        var closed = (await fixture.RecordAsync(sample, title: title)).Last(update => update.Kind == "Closed");
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(Path.GetFileName(closed.RelativePath)) < 255);
        Assert.True(File.Exists(Path.Combine(fixture.DirectoryPath, closed.RelativePath)));
    }

    /// <summary>Thirty-two independently archived rooms do not share file or pipeline state.</summary>
    [Fact]
    public async Task RecordsThirtyTwoConnectionsWithoutSharedPipelineState()
    {
        using var fixture = new MediaFixture();
        var source = await fixture.CreateSampleAsync(duration: 2);
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => fixture.RecordAsync(source, segmentBytes: 2000)));
        var closed = results.SelectMany(updates => updates.Where(update => update.Kind == "Closed")).ToArray();
        Assert.True(closed.Length >= 32);
        Assert.Equal(closed.Length, closed.Select(update => update.RelativePath).Distinct().Count());
        Assert.All(results, updates => Assert.InRange(updates.Where(update => update.Kind == "Closed").Sum(update => update.DurationMs), 1900, 2300));
    }

    /// <summary>A safety-reserve change while streaming closes the active archive before failing.</summary>
    [Fact]
    public async Task DiskReserveIsCheckedDuringRecording()
    {
        using var fixture = new MediaFixture();
        var source = await fixture.CreateSampleAsync(duration: 10);
        using var client = new HttpClient(new ThrottledRecordingHttpHandler(await File.ReadAllBytesAsync(source)));
        var recorder = new FlvMediaRecorder(client, Microsoft.Extensions.Options.Options.Create(new MediaToolOptions()), fixture.Settings);
        var updates = new List<Yohuke.LiveRecorder.Application.Media.MediaRecordingUpdate>();
        var started = DateTime.UtcNow;
        await Assert.ThrowsAsync<InsufficientMediaSpaceException>(() => recorder.RecordAsync(new()
        {
            StreamUrl = "https://example.invalid/live.flv",
            OutputDirectory = fixture.DirectoryPath,
            Title = "disk-reserve",
            StartedAtUtc = started,
            SessionStartedAtUtc = started
        }, new InlineProgress<Yohuke.LiveRecorder.Application.Media.MediaRecordingUpdate>(update =>
        {
            updates.Add(update);
            if (update.Kind == "Committed" && update.DurationMs > 0)
            {
                fixture.Settings.Value.MinimumFreeBytes = long.MaxValue;
            }
        }), CancellationToken.None));
        var closed = Assert.Single(updates, update => update.Kind == "Closed");
        Assert.True(closed.CommittedBytes > 0);
        Assert.True((await fixture.Processor.ProbeAsync(Path.Combine(fixture.DirectoryPath, closed.RelativePath))).DurationMs > 0);
    }

    /// <summary>Splits at GOP boundaries and publishes fully readable files and indexes.</summary>
    [Fact]
    public async Task RecordsSafeSegmentsWithCompleteKeyframeIndex()
    {
        using var fixture = new MediaFixture();
        var sample = await fixture.CreateSampleAsync();
        var updates = await fixture.RecordAsync(sample, segmentBytes: 1000);
        var closed = updates.Where(update => update.Kind == "Closed").ToArray();
        Assert.True(closed.Length >= 2);
        Assert.InRange(closed.Sum(update => update.DurationMs), 3900, 4300);
        foreach (var segment in closed)
        {
            Assert.True(segment.CommittedBytes > 0);
            var path = Path.Combine(fixture.DirectoryPath, segment.RelativePath);
            var probe = await fixture.Processor.ProbeAsync(path);
            Assert.Equal("h264", probe.VideoCodec);
            var index = await File.ReadAllLinesAsync(Path.Combine(fixture.DirectoryPath, segment.IndexRelativePath));
            Assert.Contains(index, line => JsonDocument.Parse(line).RootElement.GetProperty("kind").GetString() == "Keyframe");
        }
        Assert.Equal(0, closed[0].StartOffsetMs);
        Assert.True(closed[^1].StartOffsetMs > closed[0].StartOffsetMs);
    }

    /// <summary>A canceled connection closes any files already created.</summary>
    [Fact]
    public async Task PreCanceledRecordingDoesNotCreateOutput()
    {
        using var fixture = new MediaFixture();
        var sample = await fixture.CreateSampleAsync();
        using var client = new HttpClient(new RecordingHttpHandler(await File.ReadAllBytesAsync(sample)));
        var recorder = new FlvMediaRecorder(client, Microsoft.Extensions.Options.Options.Create(new MediaToolOptions()), fixture.Settings);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recorder.RecordAsync(new()
        {
            StreamUrl = "https://example.invalid/live.flv",
            OutputDirectory = fixture.DirectoryPath
        }, new InlineProgress<Yohuke.LiveRecorder.Application.Media.MediaRecordingUpdate>(_ => { }), cancellation.Token));
        Assert.False(Directory.Exists(Path.Combine(fixture.DirectoryPath, "source")));
    }
}
