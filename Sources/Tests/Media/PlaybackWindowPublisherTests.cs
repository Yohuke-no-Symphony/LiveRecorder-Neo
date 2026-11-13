using Xunit;

namespace Yohuke.LiveRecorder.Media.Tests;

/// <summary>Verifies complete playback publication without starting media tools.</summary>
public sealed class PlaybackWindowPublisherTests : IDisposable
{
    private string RootDirectory { get; } = Path.Combine(Path.GetTempPath(), "live-recorder-publisher-tests", Guid.NewGuid().ToString("N"));
    private string TemporaryDirectory => Path.Combine(RootDirectory, "window.building");
    private string OutputDirectory => Path.Combine(RootDirectory, "window");

    /// <summary>Creates an isolated staging directory for each publication test.</summary>
    public PlaybackWindowPublisherTests()
    {
        Directory.CreateDirectory(TemporaryDirectory);
    }

    /// <summary>Publishes all playback and companion files together and consumes the staging directory.</summary>
    [Fact]
    public void PublishesCompleteWindowWithMetadataFiles()
    {
        var contents = new Dictionary<string, string>
        {
            ["index.m3u8"] = "#EXTM3U\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:1,\nchunk_00006.m4s\n#EXT-X-ENDLIST\n",
            ["init.mp4"] = "initialization",
            ["chunk_00006.m4s"] = "fragment",
            ["._chunk_00006.m4s"] = "companion metadata"
        };
        foreach (var (name, content) in contents)
        {
            File.WriteAllText(Path.Combine(TemporaryDirectory, name), content);
        }

        PlaybackWindowPublisher.Publish(TemporaryDirectory, OutputDirectory);

        Assert.False(Directory.Exists(TemporaryDirectory));
        foreach (var (name, content) in contents)
        {
            Assert.Equal(content, File.ReadAllText(Path.Combine(OutputDirectory, name)));
        }
        Assert.Equal(contents.Count, Directory.EnumerateFileSystemEntries(OutputDirectory).Count());
    }

    /// <summary>Rejects both empty and populated destinations without mixing or removing their contents.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingDirectoryIsPreserved(bool containsWindow)
    {
        Directory.CreateDirectory(OutputDirectory);
        var existingFile = Path.Combine(OutputDirectory, "window.json");
        if (containsWindow)
        {
            File.WriteAllText(existingFile, "existing window");
        }
        var existingEntries = Directory.GetFileSystemEntries(OutputDirectory);
        var fragment = Path.Combine(TemporaryDirectory, "chunk_00006.m4s");
        File.WriteAllText(fragment, "new fragment");

        Assert.Throws<IOException>(() => PlaybackWindowPublisher.Publish(TemporaryDirectory, OutputDirectory));

        Assert.Equal(existingEntries, Directory.GetFileSystemEntries(OutputDirectory));
        if (containsWindow)
        {
            Assert.Equal("existing window", File.ReadAllText(existingFile));
        }
        Assert.Equal("new fragment", File.ReadAllText(fragment));
    }

    /// <summary>A destination file is preserved and leaves the staged window available for cleanup.</summary>
    [Fact]
    public void ExistingFileIsPreserved()
    {
        File.WriteAllText(OutputDirectory, "existing file");
        var fragment = Path.Combine(TemporaryDirectory, "chunk_00006.m4s");
        File.WriteAllText(fragment, "new fragment");

        Assert.Throws<IOException>(() => PlaybackWindowPublisher.Publish(TemporaryDirectory, OutputDirectory));

        Assert.Equal("existing file", File.ReadAllText(OutputDirectory));
        Assert.Equal("new fragment", File.ReadAllText(fragment));
    }

    /// <summary>A missing staged window never creates a visible output directory.</summary>
    [Fact]
    public void MissingTemporaryDirectoryDoesNotPublish()
    {
        Directory.Delete(TemporaryDirectory);

        Assert.Throws<DirectoryNotFoundException>(() => PlaybackWindowPublisher.Publish(TemporaryDirectory, OutputDirectory));

        Assert.False(Directory.Exists(OutputDirectory));
    }

    /// <summary>Removes every file and directory created by the test.</summary>
    public void Dispose()
    {
        Directory.Delete(RootDirectory, recursive: true);
    }
}
