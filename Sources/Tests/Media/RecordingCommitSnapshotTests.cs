using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Xunit;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Media.Tests;

/// <summary>Checks durable recording boundaries before the recording Worker publishes them.</summary>
public sealed class RecordingCommitSnapshotTests
{
    /// <summary>Each persisted boundary references immutable metadata and a complete matching index.</summary>
    [Fact]
    public async Task CapturesImmutablePrefixesAndMatchingIndexBoundaries()
    {
        using var fixture = new MediaFixture();
        var source = await fixture.CreateSampleAsync(duration: 4);
        var updates = await fixture.RecordAsync(source, captureCommitSnapshots: true);
        var commits = updates.Where(update => update.CommitSnapshot is not null).Select(update => update.CommitSnapshot!).ToArray();
        Assert.True(commits.Length >= 3);
        Assert.True(commits.Select(commit => commit.PrefixSha256).Distinct().Count() >= 2);
        foreach (var group in commits.GroupBy(commit => commit.SegmentId))
        {
            Assert.Equal(Enumerable.Range(1, group.Count()).Select(value => (long)value), group.Select(commit => commit.CommitSequence));
        }
        foreach (var commit in commits)
        {
            var prefix = await File.ReadAllBytesAsync(Path.Combine(fixture.DirectoryPath, commit.PrefixRelativePath));
            Assert.Equal(commit.PrefixLength, prefix.Length);
            Assert.Equal(commit.PrefixSha256, Convert.ToHexString(SHA256.HashData(prefix)).ToLowerInvariant());
            Assert.Equal("FLV", Encoding.ASCII.GetString(prefix, 0, 3));
            var indexBytes = await File.ReadAllBytesAsync(Path.Combine(fixture.DirectoryPath, commit.IndexRelativePath));
            var committedIndex = indexBytes.AsSpan(0, checked((int)commit.IndexCommittedBytes)).ToArray();
            Assert.Equal((byte)'\n', committedIndex[^1]);
            var last = Encoding.UTF8.GetString(committedIndex).Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1];
            using var document = JsonDocument.Parse(last);
            Assert.Equal("Committed", document.RootElement.GetProperty("kind").GetString());
            Assert.Equal(commit.MediaCommittedBytes, document.RootElement.GetProperty("offset").GetInt64());
        }
        var closed = Assert.Single(commits, commit => commit.IsClosed);
        var latestPath = Path.Combine(fixture.DirectoryPath, "录制提交", closed.SegmentId, "latest.json");
        var latest = JsonSerializer.Deserialize<MediaRecordingCommitSnapshot>(await File.ReadAllTextAsync(latestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(closed, latest);
    }

    /// <summary>Earlier published commits remain independently decodable after subsequent metadata rewrites.</summary>
    [Fact]
    public async Task ReplaysCommittedPrefixesIntoDecodableRecordings()
    {
        using var fixture = new MediaFixture();
        var source = await fixture.CreateSampleAsync(duration: 4);
        var updates = await fixture.RecordAsync(source, captureCommitSnapshots: true);
        var commits = updates.Select(update => update.CommitSnapshot).OfType<MediaRecordingCommitSnapshot>()
            .Where(commit => commit.DurationMs >= 900).ToArray();
        Assert.True(commits.Length >= 2);
        foreach (var commit in new[] { commits[0], commits[^1] })
        {
            var media = await File.ReadAllBytesAsync(Path.Combine(fixture.DirectoryPath, commit.RelativePath));
            var replay = media.AsSpan(0, checked((int)commit.MediaCommittedBytes)).ToArray();
            var prefix = await File.ReadAllBytesAsync(Path.Combine(fixture.DirectoryPath, commit.PrefixRelativePath));
            prefix.CopyTo(replay, 0);
            var path = Path.Combine(fixture.DirectoryPath, $"replay-{commit.CommitSequence}.flv");
            await File.WriteAllBytesAsync(path, replay);
            Assert.True((await fixture.Processor.ProbeAsync(path)).DurationMs > 0);
            await fixture.Processes.RunAsync(MediaProcessKind.Playback,
                ["-v", "error", "-xerror", "-i", path, "-f", "null", "-"], CancellationToken.None);
        }
    }

    /// <summary>GOP splits seal the final physical file and index rather than the preceding commit.</summary>
    [Fact]
    public async Task SealsEverySplitWithItsFinalLengths()
    {
        using var fixture = new MediaFixture();
        var source = await fixture.CreateSampleAsync(duration: 4);
        var updates = await fixture.RecordAsync(source, segmentBytes: 1000, captureCommitSnapshots: true);
        var closed = updates.Where(update => update.Kind == "Closed").ToArray();
        Assert.True(closed.Length >= 2);
        foreach (var update in closed)
        {
            var commit = Assert.IsType<MediaRecordingCommitSnapshot>(update.CommitSnapshot);
            Assert.True(commit.IsClosed);
            Assert.Equal(new FileInfo(Path.Combine(fixture.DirectoryPath, update.RelativePath)).Length, commit.MediaCommittedBytes);
            Assert.Equal(new FileInfo(Path.Combine(fixture.DirectoryPath, update.IndexRelativePath)).Length, commit.IndexCommittedBytes);
            var final = await File.ReadAllBytesAsync(Path.Combine(fixture.DirectoryPath, update.RelativePath));
            var prefix = await File.ReadAllBytesAsync(Path.Combine(fixture.DirectoryPath, commit.PrefixRelativePath));
            Assert.Equal(final.AsSpan(0, prefix.Length).ToArray(), prefix);
        }
    }

    /// <summary>Cancellation keeps the final committed files and a replayable closed snapshot.</summary>
    [Fact]
    public async Task CancellationSealsAndRetainsRecordedMedia()
    {
        using var fixture = new MediaFixture();
        var source = await fixture.CreateSampleAsync(duration: 4);
        using var client = new HttpClient(new RecordingHttpHandler(await File.ReadAllBytesAsync(source)));
        var recorder = new FlvMediaRecorder(client, Options.Create(new MediaToolOptions()), fixture.Settings);
        using var cancellation = new CancellationTokenSource();
        var updates = new List<MediaRecordingUpdate>();
        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recorder.RecordAsync(new()
        {
            StreamUrl = "https://example.invalid/live.flv",
            OutputDirectory = fixture.DirectoryPath,
            StartedAtUtc = started,
            SessionStartedAtUtc = started,
            CaptureCommitSnapshots = true
        }, new InlineProgress<MediaRecordingUpdate>(update =>
        {
            updates.Add(update);
            if (update.Kind == "Committed" && update.DurationMs > 900)
            {
                cancellation.Cancel();
            }
        }), cancellation.Token));
        var closed = Assert.Single(updates, update => update.Kind == "Closed").CommitSnapshot!;
        Assert.True(closed.IsClosed);
        Assert.Equal(new FileInfo(Path.Combine(fixture.DirectoryPath, closed.RelativePath)).Length, closed.MediaCommittedBytes);
        Assert.True(File.Exists(Path.Combine(fixture.DirectoryPath, closed.PrefixRelativePath)));
    }

    /// <summary>Existing local recording does not create a spool or metadata snapshot files.</summary>
    [Fact]
    public async Task LocalRecordingKeepsSnapshotsDisabledByDefault()
    {
        using var fixture = new MediaFixture();
        var source = await fixture.CreateSampleAsync(duration: 2);
        var updates = await fixture.RecordAsync(source);
        Assert.All(updates, update => Assert.Null(update.CommitSnapshot));
        Assert.False(Directory.Exists(Path.Combine(fixture.DirectoryPath, "录制提交")));
    }

    /// <summary>Metadata larger than a control frame remains a file rather than growing the JSON descriptor.</summary>
    [Fact]
    public void LargeMetadataIsStoredOutsideTheSnapshotDescriptor()
    {
        using var fixture = new MediaFixture();
        using var media = new FileStream(Path.Combine(fixture.DirectoryPath, "large.flv"), FileMode.CreateNew, FileAccess.ReadWrite);
        using var index = new StreamWriter(new FileStream(Path.Combine(fixture.DirectoryPath, "large.index.jsonl"), FileMode.CreateNew, FileAccess.Write));
        var prefix = CreatePrefix(2 * 1024 * 1024);
        media.Write(prefix);
        media.Flush();
        var state = CreateState(media, index);
        var commit = new RecordingCommitSnapshotStore(fixture.DirectoryPath).Write(state, isClosed: false);
        Assert.Equal(prefix.Length, commit.PrefixLength);
        Assert.Equal(prefix, File.ReadAllBytes(Path.Combine(fixture.DirectoryPath, commit.PrefixRelativePath)));
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(commit).Length, 1, 2048);
    }

    /// <summary>An incomplete metadata rewrite cannot replace the last durable commit.</summary>
    [Fact]
    public void InvalidPrefixLeavesThePreviousCommitIntact()
    {
        using var fixture = new MediaFixture();
        using var media = new FileStream(Path.Combine(fixture.DirectoryPath, "invalid.flv"), FileMode.CreateNew, FileAccess.ReadWrite);
        using var index = new StreamWriter(new FileStream(Path.Combine(fixture.DirectoryPath, "invalid.index.jsonl"), FileMode.CreateNew, FileAccess.Write));
        media.Write(CreatePrefix(64));
        media.Flush();
        var state = CreateState(media, index);
        var store = new RecordingCommitSnapshotStore(fixture.DirectoryPath);
        var commit = store.Write(state, isClosed: false);
        var latestPath = Path.Combine(fixture.DirectoryPath, "录制提交", commit.SegmentId, "latest.json");
        var previous = File.ReadAllBytes(latestPath);
        media.Position = 13;
        media.WriteByte(9);
        media.Flush();
        Assert.Throws<InvalidDataException>(() => store.Write(state, isClosed: false));
        Assert.Equal(previous, File.ReadAllBytes(latestPath));
        Assert.Equal(1, state.CommitSequence);
    }

    private static byte[] CreatePrefix(int payloadLength)
    {
        var prefix = new byte[28 + payloadLength];
        "FLV"u8.CopyTo(prefix);
        prefix[3] = 1;
        prefix[4] = 5;
        prefix[8] = 9;
        prefix[13] = 18;
        prefix[14] = (byte)(payloadLength >> 16);
        prefix[15] = (byte)(payloadLength >> 8);
        prefix[16] = (byte)payloadLength;
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(prefix.Length - 4), (uint)(11 + payloadLength));
        return prefix;
    }

    private static FlvOutputState CreateState(FileStream media, StreamWriter index)
    {
        return new()
        {
            Id = Guid.NewGuid().ToString("N"),
            RelativePath = Path.GetFileName(media.Name),
            IndexRelativePath = Path.GetFileName(((FileStream)index.BaseStream).Name),
            Path = media.Name,
            IndexPath = ((FileStream)index.BaseStream).Name,
            Stream = media,
            IndexWriter = index,
            CommittedBytes = media.Length
        };
    }
}
