using Microsoft.Extensions.Options;
using Xunit;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Media.Tests;

/// <summary>Exercises indexed playback windows independently from an HTTP server.</summary>
public sealed class PlaybackWindowTests
{
    /// <summary>An open archive is read only through its published commit boundary.</summary>
    [Fact]
    public async Task OpenFileWindowStopsAtCommittedBytes()
    {
        using var fixture = new MediaFixture();
        var sample = await fixture.CreateSampleAsync(duration: 8);
        var updates = await fixture.RecordAsync(sample);
        var committed = updates.First(update => update.Kind == "Committed" && update.DurationMs > 0);
        var path = Path.Combine(fixture.DirectoryPath, committed.RelativePath);
        using var openWriter = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var builder = new IndexedPlaybackWindowBuilder(fixture.Processes, Options.Create(new MediaToolOptions()), Options.Create(new PlaybackWindowOptions()));
        var result = await builder.BuildAsync(new()
        {
            SourcePath = path,
            IndexPath = Path.Combine(fixture.DirectoryPath, committed.IndexRelativePath),
            OutputDirectory = Path.Combine(fixture.DirectoryPath, "open-window"),
            SourceStartMs = 0,
            DurationMs = 120000,
            CommittedBytes = committed.CommittedBytes,
            IsClosed = false
        }, CancellationToken.None);
        Assert.InRange(result.DurationMs, 500, 1600);
        Assert.InRange((await fixture.Processor.ProbeAsync(result.PlaylistPath)).DurationMs, 500, 1600);
    }

    /// <summary>Positions beyond the archive commit are pending rather than empty successful videos.</summary>
    [Fact]
    public async Task UncommittedPositionIsRejected()
    {
        using var fixture = new MediaFixture();
        var sample = await fixture.CreateSampleAsync(duration: 2);
        var segment = (await fixture.RecordAsync(sample)).Last(update => update.Kind == "Closed");
        var builder = new IndexedPlaybackWindowBuilder(fixture.Processes, Options.Create(new MediaToolOptions()), Options.Create(new PlaybackWindowOptions()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(new()
        {
            SourcePath = Path.Combine(fixture.DirectoryPath, segment.RelativePath),
            IndexPath = Path.Combine(fixture.DirectoryPath, segment.IndexRelativePath),
            OutputDirectory = Path.Combine(fixture.DirectoryPath, "pending-window"),
            SourceStartMs = segment.DurationMs + 500,
            CommittedBytes = segment.CommittedBytes,
            IsClosed = false
        }, CancellationToken.None));
    }

    /// <summary>A failed publication preserves the existing window and removes its temporary output.</summary>
    [Fact]
    public async Task FailedPublicationPreservesExistingWindowAndCleansTemporaryDirectory()
    {
        using var fixture = new MediaFixture();
        var sample = await fixture.CreateSampleAsync(duration: 2);
        var segment = (await fixture.RecordAsync(sample)).Last(update => update.Kind == "Closed");
        var directory = Path.Combine(fixture.DirectoryPath, "existing-window");
        Directory.CreateDirectory(directory);
        var existingFile = Path.Combine(directory, "window.json");
        await File.WriteAllTextAsync(existingFile, "existing window");
        var builder = new IndexedPlaybackWindowBuilder(fixture.Processes, Options.Create(new MediaToolOptions()), Options.Create(new PlaybackWindowOptions()));

        await Assert.ThrowsAsync<IOException>(() => builder.BuildAsync(new()
        {
            SourcePath = Path.Combine(fixture.DirectoryPath, segment.RelativePath),
            IndexPath = Path.Combine(fixture.DirectoryPath, segment.IndexRelativePath),
            OutputDirectory = directory,
            SourceStartMs = 0,
            DurationMs = 1000,
            CommittedBytes = segment.CommittedBytes,
            IsClosed = true
        }, CancellationToken.None));

        Assert.Equal("existing window", await File.ReadAllTextAsync(existingFile));
        Assert.Equal(existingFile, Assert.Single(Directory.EnumerateFileSystemEntries(directory)));
        Assert.Empty(Directory.EnumerateDirectories(fixture.DirectoryPath, "existing-window.building-*"));
    }

    /// <summary>Starts near a requested keyframe and produces an independently playable window.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BuildsIndexedWindowNearRequestedPosition(bool transcode, bool trailingDirectorySeparator)
    {
        using var fixture = new MediaFixture();
        var sample = await fixture.CreateSampleAsync(duration: 8);
        var segment = (await fixture.RecordAsync(sample)).Last(update => update.Kind == "Closed");
        var builder = new IndexedPlaybackWindowBuilder(fixture.Processes, Options.Create(new MediaToolOptions()), Options.Create(new PlaybackWindowOptions()));
        var directory = Path.Combine(fixture.DirectoryPath, "window");
        var result = await builder.BuildAsync(new()
        {
            SourcePath = Path.Combine(fixture.DirectoryPath, segment.RelativePath),
            IndexPath = Path.Combine(fixture.DirectoryPath, segment.IndexRelativePath),
            OutputDirectory = trailingDirectorySeparator ? directory + Path.DirectorySeparatorChar : directory,
            SourceStartMs = 5500,
            DurationMs = 1500,
            CommittedBytes = segment.CommittedBytes,
            IsClosed = true,
            Transcode = transcode
        }, CancellationToken.None);
        Assert.InRange(result.SourceStartMs, 4500, 5500);
        Assert.True(File.Exists(result.PlaylistPath));
        var playlist = await File.ReadAllTextAsync(result.PlaylistPath);
        Assert.Contains("#EXT-X-MAP", playlist);
        Assert.Contains("#EXT-X-ENDLIST", playlist);
        Assert.True(File.Exists(Path.Combine(directory, "init.mp4")));
        var fragments = playlist.Split('\n').Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();
        Assert.NotEmpty(fragments);
        Assert.All(fragments, fragment => Assert.True(File.Exists(Path.Combine(directory, fragment))));
        Assert.Empty(Directory.EnumerateDirectories(fixture.DirectoryPath, "*.building-*", SearchOption.AllDirectories));
        Assert.InRange(result.DurationMs, 1500, 3500);
        var probe = await fixture.Processor.ProbeAsync(result.PlaylistPath);
        Assert.Equal("h264", probe.VideoCodec);
        Assert.Equal("aac", probe.AudioCodec);
    }

    /// <summary>The shared duration caps work even when a caller requests a longer window.</summary>
    [Fact]
    public async Task UsesSharedWindowDurationToBoundGeneration()
    {
        using var fixture = new MediaFixture();
        var sample = await fixture.CreateSampleAsync(duration: 8);
        var segment = (await fixture.RecordAsync(sample)).Last(update => update.Kind == "Closed");
        var builder = new IndexedPlaybackWindowBuilder(fixture.Processes, Options.Create(new MediaToolOptions()),
            Options.Create(new PlaybackWindowOptions { Duration = TimeSpan.FromSeconds(2) }));

        var result = await builder.BuildAsync(new()
        {
            SourcePath = Path.Combine(fixture.DirectoryPath, segment.RelativePath),
            IndexPath = Path.Combine(fixture.DirectoryPath, segment.IndexRelativePath),
            OutputDirectory = Path.Combine(fixture.DirectoryPath, "bounded-window"),
            SourceStartMs = 3000,
            DurationMs = 120000,
            CommittedBytes = segment.CommittedBytes,
            IsClosed = true
        }, CancellationToken.None);

        Assert.InRange(result.SourceStartMs, 2000, 3000);
        Assert.InRange(result.SourceEndMs, 5000, 6100);
        Assert.False(result.ReachesCommittedEnd);
    }

    /// <summary>Long GOP windows report their actual boundaries and allow the next window to start without a gap.</summary>
    [Fact]
    public async Task LongGopReportsActualCoverageAndContinuesAtReturnedEnd()
    {
        using var fixture = new MediaFixture();
        var sample = await fixture.CreateSampleAsync(duration: 18, keyframeInterval: 60);
        var segment = (await fixture.RecordAsync(sample)).Last(update => update.Kind == "Closed");
        var builder = new IndexedPlaybackWindowBuilder(fixture.Processes, Options.Create(new MediaToolOptions()),
            Options.Create(new PlaybackWindowOptions { Duration = TimeSpan.FromSeconds(1) }));
        var request = new PlaybackWindowRequest
        {
            SourcePath = Path.Combine(fixture.DirectoryPath, segment.RelativePath),
            IndexPath = Path.Combine(fixture.DirectoryPath, segment.IndexRelativePath),
            OutputDirectory = Path.Combine(fixture.DirectoryPath, "long-gop-first"),
            SourceStartMs = 7000,
            DurationMs = 1000,
            CommittedBytes = segment.CommittedBytes,
            IsClosed = true
        };

        var first = await builder.BuildAsync(request, CancellationToken.None);
        request.SourceStartMs = first.SourceEndMs;
        request.OutputDirectory = Path.Combine(fixture.DirectoryPath, "long-gop-next");
        var next = await builder.BuildAsync(request, CancellationToken.None);

        Assert.InRange(first.SourceStartMs, 5900, 6100);
        Assert.InRange(first.SourceEndMs, 11900, 12100);
        Assert.True(first.SourceEndMs - first.SourceStartMs > request.DurationMs);
        Assert.Equal(first.SourceEndMs, next.SourceStartMs);
        Assert.True(next.ReachesCommittedEnd);
        Assert.Equal("h264", (await fixture.Processor.ProbeAsync(first.PlaylistPath)).VideoCodec);
        Assert.Equal("h264", (await fixture.Processor.ProbeAsync(next.PlaylistPath)).VideoCodec);
    }
}
