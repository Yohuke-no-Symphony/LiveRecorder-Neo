using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Xunit;

namespace Yohuke.LiveRecorder.Media.Tests;

/// <summary>验证封面抽帧只读取已提交媒体并输出可解码的独立图片。</summary>
public sealed class VideoFrameTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtractsCommittedFrameWithoutChangingRecording(bool isClosed)
    {
        using var fixture = new MediaFixture();
        var sample = await fixture.CreateSampleAsync(size: "1280x720", duration: 3);
        var updates = await fixture.RecordAsync(sample);
        var segment = isClosed
            ? updates.Last(update => update.Kind == "Closed")
            : updates.First(update => update.Kind == "Committed" && update.DurationMs > 0);
        var source = Path.Combine(fixture.DirectoryPath, segment.RelativePath);
        var original = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var output = Path.Combine(fixture.DirectoryPath, "cover.jpg");
        var extractor = new FfmpegVideoFrameExtractor(fixture.Processes, Options.Create(new MediaToolOptions()));

        Assert.True(await extractor.TryExtractAsync(new()
        {
            SourcePath = source,
            IndexPath = Path.Combine(fixture.DirectoryPath, segment.IndexRelativePath),
            CommittedBytes = segment.CommittedBytes,
            IsClosed = isClosed,
            OutputPath = output
        }, CancellationToken.None));

        var probe = await fixture.Processor.ProbeAsync(output);
        Assert.Equal("mjpeg", probe.VideoCodec);
        Assert.Equal(640, probe.Width);
        Assert.Equal(360, probe.Height);
        await MediaProcessRunner.RunProcessAsync("ffmpeg",
            ["-hide_banner", "-loglevel", "error", "-xerror", "-i", output, "-f", "null", "-"], CancellationToken.None);
        Assert.Equal(original, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        Assert.Empty(Directory.EnumerateDirectories(fixture.DirectoryPath, ".frame-*"));
    }

    [Fact]
    public async Task DoesNotReadUncommittedVideoOrPublishEmptyImage()
    {
        using var fixture = new MediaFixture();
        var sample = await fixture.CreateSampleAsync();
        var segment = (await fixture.RecordAsync(sample)).Last(update => update.Kind == "Closed");
        var output = Path.Combine(fixture.DirectoryPath, "pending.jpg");
        var extractor = new FfmpegVideoFrameExtractor(fixture.Processes, Options.Create(new MediaToolOptions()));

        Assert.False(await extractor.TryExtractAsync(new()
        {
            SourcePath = Path.Combine(fixture.DirectoryPath, segment.RelativePath),
            IndexPath = Path.Combine(fixture.DirectoryPath, segment.IndexRelativePath),
            CommittedBytes = 13,
            IsClosed = false,
            OutputPath = output
        }, CancellationToken.None));

        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingIndexOnlyAllowsClosedRecording(bool isClosed)
    {
        using var fixture = new MediaFixture();
        var sample = await fixture.CreateSampleAsync();
        var output = Path.Combine(fixture.DirectoryPath, "legacy.jpg");
        var extractor = new FfmpegVideoFrameExtractor(fixture.Processes, Options.Create(new MediaToolOptions()));

        Assert.Equal(isClosed, await extractor.TryExtractAsync(new()
        {
            SourcePath = sample,
            IsClosed = isClosed,
            CommittedBytes = new FileInfo(sample).Length,
            OutputPath = output
        }, CancellationToken.None));
        Assert.Equal(isClosed, File.Exists(output));
    }

    [Fact]
    public async Task FailedExtractionPreservesExistingOutputAndCleansTemporaryFiles()
    {
        using var fixture = new MediaFixture();
        var source = Path.Combine(fixture.DirectoryPath, "corrupt.flv");
        var output = Path.Combine(fixture.DirectoryPath, "cover.jpg");
        await File.WriteAllTextAsync(source, "corrupt recording");
        await File.WriteAllTextAsync(output, "existing cover");
        var extractor = new FfmpegVideoFrameExtractor(fixture.Processes, Options.Create(new MediaToolOptions()));

        await Assert.ThrowsAsync<MediaToolException>(() => extractor.TryExtractAsync(new()
        {
            SourcePath = source,
            IsClosed = true,
            OutputPath = output
        }, CancellationToken.None));

        Assert.Equal("existing cover", await File.ReadAllTextAsync(output));
        Assert.Empty(Directory.EnumerateDirectories(fixture.DirectoryPath, ".frame-*"));
    }

    [Fact]
    public async Task MissingIndexStillHonorsClosedRecordingCommitBoundary()
    {
        using var fixture = new MediaFixture();
        var sample = await fixture.CreateSampleAsync();
        var output = Path.Combine(fixture.DirectoryPath, "uncommitted.jpg");
        var extractor = new FfmpegVideoFrameExtractor(fixture.Processes, Options.Create(new MediaToolOptions()));

        await Assert.ThrowsAsync<MediaToolException>(() => extractor.TryExtractAsync(new()
        {
            SourcePath = sample,
            IsClosed = true,
            CommittedBytes = 14,
            OutputPath = output
        }, CancellationToken.None));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task DeadlineWhileQueuedPreservesOutputAndReleasesTemporaryFiles()
    {
        using var fixture = new MediaFixture();
        fixture.Settings.Value.MediaConcurrency = 1;
        var sample = await fixture.CreateSampleAsync();
        var output = Path.Combine(fixture.DirectoryPath, "cover.jpg");
        await File.WriteAllTextAsync(output, "existing cover");
        using var blockerCancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = fixture.Processes.RunAsync(MediaProcessKind.Export,
            ["-hide_banner", "-loglevel", "error", "-f", "flv", "-i", "pipe:0", "-f", "null", "-"], blockerCancellation.Token,
            input: async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var extractor = new FfmpegVideoFrameExtractor(fixture.Processes,
                Options.Create(new MediaToolOptions { FrameExtractionTimeout = TimeSpan.FromMilliseconds(200) }));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extractor.TryExtractAsync(new()
            {
                SourcePath = sample,
                IsClosed = true,
                OutputPath = output
            }, CancellationToken.None));

            Assert.Equal("existing cover", await File.ReadAllTextAsync(output));
            Assert.Empty(Directory.EnumerateDirectories(fixture.DirectoryPath, ".frame-*"));
        }
        finally
        {
            await blockerCancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocker);
        }
        var recovered = new FfmpegVideoFrameExtractor(fixture.Processes, Options.Create(new MediaToolOptions()));
        Assert.True(await recovered.TryExtractAsync(new()
        {
            SourcePath = sample,
            IsClosed = true,
            OutputPath = output
        }, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationDoesNotCreateOutput()
    {
        using var fixture = new MediaFixture();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var output = Path.Combine(fixture.DirectoryPath, "cancelled.jpg");
        var extractor = new FfmpegVideoFrameExtractor(fixture.Processes, Options.Create(new MediaToolOptions()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extractor.TryExtractAsync(new()
        {
            SourcePath = "unused.flv",
            OutputPath = output
        }, cancellation.Token));

        Assert.False(File.Exists(output));
    }
}
