using Microsoft.Extensions.Options;
using Xunit;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Media.Tests;

/// <summary>Exercises speculative playback admission, failure cleanup, and cancellation with real media processes.</summary>
public sealed class PlaybackPrefetchTests
{
    /// <summary>Unavailable prefetch capacity is reported before accessing any source files.</summary>
    [Fact]
    public async Task UnavailableCapacitySkipsMediaIo()
    {
        using var fixture = new MediaFixture();
        fixture.Settings.Value.PlaybackConcurrency = 1;
        var builder = CreateBuilder(fixture);
        var output = Path.Combine(fixture.DirectoryPath, "skipped-window");

        var result = await builder.TryPrefetchAsync(new()
        {
            SourcePath = Path.Combine(fixture.DirectoryPath, "missing.flv"),
            IndexPath = Path.Combine(fixture.DirectoryPath, "missing.index"),
            OutputDirectory = output
        }, CancellationToken.None);

        Assert.Null(result);
        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateDirectories(fixture.DirectoryPath, "*.building-*"));
    }

    /// <summary>A failed speculative build releases capacity for a subsequent valid window.</summary>
    [Fact]
    public async Task FailedPrefetchReleasesItsReservation()
    {
        using var fixture = new MediaFixture();
        var builder = CreateBuilder(fixture);
        await Assert.ThrowsAsync<FileNotFoundException>(() => builder.TryPrefetchAsync(new()
        {
            SourcePath = Path.Combine(fixture.DirectoryPath, "missing.flv")
        }, CancellationToken.None));
        var sample = await fixture.CreateSampleAsync(duration: 3);
        var segment = (await fixture.RecordAsync(sample)).Last(update => update.Kind == "Closed");

        var result = await builder.TryPrefetchAsync(new()
        {
            SourcePath = Path.Combine(fixture.DirectoryPath, segment.RelativePath),
            IndexPath = Path.Combine(fixture.DirectoryPath, segment.IndexRelativePath),
            OutputDirectory = Path.Combine(fixture.DirectoryPath, "prefetched-window"),
            DurationMs = 1000,
            CommittedBytes = segment.CommittedBytes,
            IsClosed = true
        }, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("#EXT-X-ENDLIST", await File.ReadAllTextAsync(result.PlaylistPath));
        Assert.Equal("h264", (await fixture.Processor.ProbeAsync(result.PlaylistPath)).VideoCodec);
        Assert.Empty(Directory.EnumerateDirectories(fixture.DirectoryPath, "*.building-*"));
    }

    /// <summary>Canceling an active speculative process terminates it and returns its process capacity.</summary>
    [Fact]
    public async Task CancelingActivePrefetchReturnsProcessCapacity()
    {
        using var fixture = new MediaFixture();
        using (var reservation = await fixture.Processes.TryAcquirePlaybackPrefetchAsync(CancellationToken.None))
        {
            Assert.NotNull(reservation);
            using var cancellation = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var process = reservation.RunAsync(
                ["-hide_banner", "-loglevel", "error", "-f", "flv", "-i", "pipe:0", "-f", "null", "-"],
                cancellation.Token,
                async (_, token) =>
                {
                    started.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        using var replacement = await fixture.Processes.TryAcquirePlaybackPrefetchAsync(CancellationToken.None);
        Assert.NotNull(replacement);
    }

    /// <summary>Canceling a window after staging begins never publishes its output and cleans the staging directory.</summary>
    [Fact]
    public async Task CanceledWindowCleansStagedOutput()
    {
        using var fixture = new MediaFixture();
        var sample = await fixture.CreateSampleAsync(duration: 8);
        var segment = (await fixture.RecordAsync(sample)).Last(update => update.Kind == "Closed");
        using (var occupied = await fixture.Processes.TryAcquirePlaybackPrefetchAsync(CancellationToken.None))
        {
            Assert.NotNull(occupied);
            fixture.Settings.Value.PlaybackConcurrency = 1;
            using var cancellation = new CancellationTokenSource();
            using var watcher = new FileSystemWatcher(fixture.DirectoryPath, "canceled-window.building-*");
            var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.Created += (_, _) => staged.TrySetResult();
            watcher.EnableRaisingEvents = true;
            var directory = Path.Combine(fixture.DirectoryPath, "canceled-window");
            var build = CreateBuilder(fixture).BuildAsync(new()
            {
                SourcePath = Path.Combine(fixture.DirectoryPath, segment.RelativePath),
                IndexPath = Path.Combine(fixture.DirectoryPath, segment.IndexRelativePath),
                OutputDirectory = directory,
                CommittedBytes = segment.CommittedBytes,
                IsClosed = true,
                Transcode = true
            }, cancellation.Token);

            await staged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => build.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.False(Directory.Exists(directory));
            Assert.Empty(Directory.EnumerateDirectories(fixture.DirectoryPath, "*.building-*"));
        }
        fixture.Settings.Value.PlaybackConcurrency = 2;
        using var replacement = await fixture.Processes.TryAcquirePlaybackPrefetchAsync(CancellationToken.None);
        Assert.NotNull(replacement);
    }

    private static IndexedPlaybackWindowBuilder CreateBuilder(MediaFixture fixture) =>
        new(fixture.Processes, Options.Create(new MediaToolOptions()), Options.Create(new PlaybackWindowOptions()));
}
