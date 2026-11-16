using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Tests;

public sealed class EventArchiveIndexCacheTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "recorder-event-index-cache-" + Guid.NewGuid().ToString("N"));
    private readonly EventArchiveReader reader = new();

    [Fact]
    public async Task UnchangedArchivesReuseValidatedBlocksAndKeepEventObjectsIndependent()
    {
        var directory = await WriteArchiveAsync("source", 300);
        var first = await ReadBlocksAsync(directory);
        var second = await ReadBlocksAsync(directory);

        Assert.Equal(3, first.Count);
        Assert.Same(first[0], second[0]);
        var page = await reader.ReadAsync(directory, 0, 10, null, null, 10);
        page.Items[0].OffsetMs = 99999;
        page.Items[0].Text = "changed";
        var repeated = await reader.ReadAsync(directory, 0, 10, null, null, 10);

        Assert.NotSame(page.Items[0], repeated.Items[0]);
        Assert.Equal(0, repeated.Items[0].OffsetMs);
        Assert.Equal("event-0", repeated.Items[0].Text);
    }

    [Fact]
    public async Task ConcurrentReadersShareIndexPreparationAndUseIndependentStreams()
    {
        var directory = await WriteArchiveAsync("source", 300);
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => ReadBlocksAsync(directory)));

        Assert.All(snapshots, blocks => Assert.Same(snapshots[0][0], blocks[0]));
        var pages = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => reader.ReadAsync(directory, index, index, null, null, 1)));
        for (var index = 0; index < pages.Length; index++)
        {
            Assert.Equal(index, Assert.Single(pages[index].Items).OffsetMs);
        }
    }

    [Fact]
    public async Task AppendedEventsReuseFullBlocksAndRebuildThePartialTail()
    {
        var directory = await WriteArchiveAsync("source", 130);
        var first = await ReadBlocksAsync(directory);
        await AppendAsync(directory, new() { Sequence = 131, OffsetMs = 9000, Text = "late" });
        var second = await ReadBlocksAsync(directory);

        Assert.Same(first[0], second[0]);
        Assert.Equal(2, first[1].Count);
        Assert.Equal(3, second[1].Count);
        Assert.NotSame(first[1], second[1]);
        var page = await reader.ReadAsync(directory, 9000, 9000, null, null, 10);
        Assert.Equal("late", Assert.Single(page.Items).Text);
    }

    [Fact]
    public async Task MissingLiveIndexReusesReconstructedBlocksWithoutPublishingOverTheWriter()
    {
        var directory = CreateDirectory("live");
        await using var writer = new SessionEventWriter(directory);
        var path = Path.Combine(directory, "事件.jsonl");
        await writer.WriteAsync(new() { OffsetMs = 1, Text = "live" });
        await WaitForLengthAsync(path, 0);
        var first = await ReadBlocksAsync(directory);
        var second = await ReadBlocksAsync(directory);

        Assert.Same(first[0], second[0]);
        Assert.Empty(await File.ReadAllTextAsync(Path.Combine(directory, "事件.index.jsonl")));
        var previousLength = new FileInfo(path).Length;
        await writer.WriteAsync(new() { OffsetMs = 2, Text = "next" });
        await WaitForLengthAsync(path, previousLength);
        var page = await reader.ReadAsync(directory, 0, 10, null, null, 10);

        Assert.Equal(new[] { "live", "next" }, page.Items.Select(item => item.Text));
        Assert.Equal(1, first[0].Count);
    }

    [Fact]
    public async Task SameLengthOverwriteInvalidatesTheIndexEvenWhenTheTimestampIsRestored()
    {
        var directory = await WriteArchiveAsync("source", 1);
        var first = await ReadBlocksAsync(directory);
        var eventPath = Path.Combine(directory, "事件.jsonl");
        var indexPath = Path.Combine(directory, "事件.index.jsonl");
        var eventTime = File.GetLastWriteTimeUtc(eventPath);
        var indexTime = File.GetLastWriteTimeUtc(indexPath);
        var source = await File.ReadAllTextAsync(eventPath);
        await File.WriteAllTextAsync(eventPath, source.Replace("\"offsetMs\":0", "\"offsetMs\":9", StringComparison.Ordinal), new UTF8Encoding(false));
        var index = await File.ReadAllTextAsync(indexPath);
        await File.WriteAllTextAsync(indexPath, index.Replace("\"minMs\":0", "\"minMs\":9", StringComparison.Ordinal).Replace("\"maxMs\":0", "\"maxMs\":9", StringComparison.Ordinal), new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(eventPath, eventTime);
        File.SetLastWriteTimeUtc(indexPath, indexTime);

        var page = await reader.ReadAsync(directory, 9, 9, null, null, 10);
        var second = await ReadBlocksAsync(directory);

        Assert.Equal(9, Assert.Single(page.Items).OffsetMs);
        Assert.NotSame(first[0], second[0]);
    }

    [Fact]
    public async Task AtomicIndexReplacementAndTruncatedArchivesInvalidateCachedBlocks()
    {
        var directory = await WriteArchiveAsync("source", 300);
        var first = await ReadBlocksAsync(directory);
        var indexPath = Path.Combine(directory, "事件.index.jsonl");
        var replacement = indexPath + ".replacement";
        await File.WriteAllTextAsync(replacement, "{broken}\n");
        File.Move(replacement, indexPath, true);
        var repaired = await reader.ReadAsync(directory, 0, 1000, null, null, 1000);

        Assert.Equal(300, repaired.Items.Count);
        Assert.NotSame(first[0], (await ReadBlocksAsync(directory))[0]);
        await using (var events = new FileStream(Path.Combine(directory, "事件.jsonl"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            events.SetLength(first[0].EndByte);
        }
        var truncated = await reader.ReadAsync(directory, 0, 1000, null, null, 1000);

        Assert.Equal(128, truncated.Items.Count);
        Assert.Equal(128, truncated.Items[^1].Sequence);
    }

    [Fact]
    public async Task SameLengthArchiveReplacementWithRestoredTimestampUsesTheNewFile()
    {
        var directory = await WriteArchiveAsync("source", 1);
        var first = await ReadBlocksAsync(directory);
        var path = Path.Combine(directory, "事件.jsonl");
        var originalTime = File.GetLastWriteTimeUtc(path);
        var replacement = path + ".replacement";
        var source = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(replacement, source.Replace("event-0", "other-0", StringComparison.Ordinal), new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(replacement, originalTime);
        File.Move(replacement, path, true);
        var page = await reader.ReadAsync(directory, 0, 10, null, null, 10);

        Assert.Equal("other-0", Assert.Single(page.Items).Text);
        Assert.NotSame(first[0], (await ReadBlocksAsync(directory))[0]);
        await File.WriteAllTextAsync(Path.Combine(directory, "事件.jsonl"), JsonSerializer.Serialize(new RecordedEventEnvelope { Sequence = 1, OffsetMs = 8000, Text = "replacement" }, JsonFiles.Options) + "\n", new UTF8Encoding(false));
        var truncated = await reader.ReadAsync(directory, 0, 10000, null, null, 1000);

        Assert.Equal("replacement", Assert.Single(truncated.Items).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MiddleRewriteWithRestoredTimestampRevalidatesCachedBlockBoundaries(bool append)
    {
        var directory = await WriteArchiveAsync("source", 300);
        var first = await ReadBlocksAsync(directory);
        var path = Path.Combine(directory, "事件.jsonl");
        var original = await File.ReadAllBytesAsync(path);
        var modified = File.GetLastWriteTimeUtc(path);
        var lines = await File.ReadAllLinesAsync(path);
        lines[127] = lines[127].Replace("event-127", "event-127x", StringComparison.Ordinal);
        lines[129] = lines[129].Replace("event-129", "event129", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, modified);
        var rewritten = await File.ReadAllBytesAsync(path);

        Assert.Equal(original.Length, rewritten.Length);
        Assert.Equal(original.Take(4096), rewritten.Take(4096));
        Assert.Equal(original.TakeLast(4096), rewritten.TakeLast(4096));
        if (append)
        {
            await AppendAsync(directory, new() { Sequence = 301, OffsetMs = 9000, Text = "appended" });
        }
        var page = await reader.ReadAsync(directory, 127, 127, null, null, 10);

        Assert.Equal("event-127x", Assert.Single(page.Items).Text);
        Assert.NotSame(first[0], (await ReadBlocksAsync(directory))[0]);
    }

    [Fact]
    public async Task CancelledPreparationLeavesSubsequentReadersAbleToBuildTheIndex()
    {
        var directory = await WriteArchiveAsync("source", 300);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(directory, 0, 1000, null, null, 1000, cancellation.Token));
        var page = await reader.ReadAsync(directory, 0, 1000, null, null, 1000);

        Assert.Equal(300, page.Items.Count);
        var first = await ReadBlocksAsync(directory);
        Assert.Same(first[0], (await ReadBlocksAsync(directory))[0]);
    }

    [Fact]
    public async Task CacheRemainsBoundedAcrossManyArchives()
    {
        for (var index = 0; index < 140; index++)
        {
            var directory = await WriteArchiveAsync("source-" + index, 1);
            await ReadBlocksAsync(directory);
        }
        var cache = (MemoryCache)typeof(EventArchiveReader).GetField("indexCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;

        Assert.InRange(cache.Count, 1, 128);
        var page = await reader.ReadAsync(Path.Combine(root, "source-0"), 0, 10, null, null, 10);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task OversizedRefreshRemovesThePreviousCachedSnapshot()
    {
        var directory = await WriteArchiveAsync("source", 17 * 128);
        var blocks = await ReadBlocksAsync(directory);
        var largeKey = new string('x', 1024 * 1024);
        foreach (var block in blocks)
        {
            block.RedPocketKeys.Add(largeKey);
        }
        using var lease = new FileStream(Path.Combine(directory, ".事件.writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await AppendAsync(directory, new() { Sequence = 17 * 128 + 1, OffsetMs = 9000, Text = "appended" });
        var refreshed = await ReadBlocksAsync(directory);
        var cache = (MemoryCache)typeof(EventArchiveReader).GetField("indexCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
        var key = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? directory.ToUpperInvariant() : directory;

        Assert.Equal(18, refreshed.Count);
        Assert.False(cache.TryGetValue(key, out _));
        var page = await reader.ReadAsync(directory, 9000, 9000, null, null, 10);
        Assert.Equal("appended", Assert.Single(page.Items).Text);
    }

    [Fact]
    public async Task CancellingAQueuedReaderPreservesOtherReadersAndThePreparedCache()
    {
        var directory = await WriteArchiveAsync("source", 300);
        var gates = (SemaphoreSlim[])typeof(EventArchiveReader).GetField("indexGates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
        var key = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? directory.ToUpperInvariant() : directory;
        var gate = gates[(int)((uint)StringComparer.Ordinal.GetHashCode(key) % gates.Length)];
        using var cancellation = new CancellationTokenSource();
        await gate.WaitAsync();
        Task<EventPage> remaining;
        try
        {
            var cancelled = reader.ReadAsync(directory, 0, 1000, null, null, 1000, cancellation.Token);
            remaining = reader.ReadAsync(directory, 0, 1000, null, null, 1000);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        }
        finally
        {
            gate.Release();
        }
        Assert.Equal(300, (await remaining).Items.Count);
        var blocks = await ReadBlocksAsync(directory);
        Assert.Same(blocks[0], (await ReadBlocksAsync(directory))[0]);
    }

    public void Dispose()
    {
        reader.Dispose();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }

    private string CreateDirectory(string name)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private async Task<string> WriteArchiveAsync(string name, int count)
    {
        var directory = CreateDirectory(name);
        await using var writer = new SessionEventWriter(directory);
        for (var index = 0; index < count; index++)
        {
            await writer.WriteAsync(new() { OffsetMs = index, Text = "event-" + index });
        }
        return directory;
    }

    private async Task<IReadOnlyList<EventIndexBlock>> ReadBlocksAsync(string directory)
    {
        var method = typeof(EventArchiveReader).GetMethod("OpenSnapshotAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pending = (Task)method.Invoke(reader, [directory, CancellationToken.None])!;
        await pending;
        var snapshot = pending.GetType().GetProperty("Result")!.GetValue(pending)!;
        await using var disposable = (IAsyncDisposable)snapshot;
        return (IReadOnlyList<EventIndexBlock>)snapshot.GetType().GetProperty("Blocks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(snapshot)!;
    }

    private static Task AppendAsync(string directory, RecordedEventEnvelope item) => File.AppendAllTextAsync(Path.Combine(directory, "事件.jsonl"), JsonSerializer.Serialize(item, JsonFiles.Options) + "\n", new UTF8Encoding(false));

    private static async Task WaitForLengthAsync(string path, long previousLength)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (new FileInfo(path).Length <= previousLength)
        {
            await Task.Delay(5, timeout.Token);
        }
    }
}
