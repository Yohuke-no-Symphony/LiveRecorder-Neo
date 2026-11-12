using System.Text;
using System.Text.Json;
using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;

namespace Yohuke.LiveRecorder.Tests;

public sealed class EventArchiveTests
{
    [Fact]
    public async Task QueueFlushesDoNotCreateAnIndexBlockPerEvent()
    {
        var directory = CreateDirectory();
        try
        {
            var path = EventPath(directory);
            await using (var writer = new SessionEventWriter(directory))
            {
                for (var index = 0; index < 64; index++)
                {
                    var previousLength = new FileInfo(path).Length;
                    await writer.WriteAsync(new() { OffsetMs = index, Text = "逐条刷盘" });
                    await WaitForLengthAsync(path, previousLength);
                }
                Assert.Empty(await ReadBlocksAsync(directory));
                for (var index = 64; index < 128; index++)
                {
                    await writer.WriteAsync(new() { OffsetMs = index });
                }
                await WaitForLengthAsync(IndexPath(directory), 0);
                var full = Assert.Single(await ReadBlocksAsync(directory));
                Assert.Equal(128, full.Count);
                Assert.Equal(128, full.LastSequence);
                var length = new FileInfo(path).Length;
                await writer.WriteAsync(new() { OffsetMs = 128 });
                await WaitForLengthAsync(path, length);
                Assert.Single(await ReadBlocksAsync(directory));
            }
            var blocks = await ReadBlocksAsync(directory);
            Assert.Equal([128, 1], blocks.Select(block => block.Count));
            Assert.Equal(129, blocks[^1].LastSequence);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task IndexReadsLateEventsAndRecoversIncompleteTail()
    {
        var directory = CreateDirectory();
        try
        {
            await using (var writer = new SessionEventWriter(directory))
            {
                for (var index = 0; index < 300; index++)
                {
                    await writer.WriteAsync(new() { OffsetMs = index * 1000, Text = "事件" + index });
                }
                await writer.WriteAsync(new() { OffsetMs = 10000, Text = "迟到事件" });
            }
            await File.AppendAllTextAsync(EventPath(directory), "{\"partial\":");
            await using (var writer = new SessionEventWriter(directory))
            {
                await writer.WriteAsync(new() { OffsetMs = 10001, Text = "恢复后" });
            }
            var result = await new EventArchiveReader().ReadAsync(directory, 10000, 10001, null, null, 100);
            Assert.Equal(3, result.Items.Count);
            Assert.Contains(result.Items, item => item.Text == "迟到事件");
            Assert.Contains(result.Items, item => item.Sequence == 302);
            Assert.Equal([128, 128, 46], (await ReadBlocksAsync(directory)).Select(block => block.Count));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task PaginationIncludesLateEventsOnceAndAppliesKindFilter()
    {
        var directory = CreateDirectory();
        try
        {
            await using (var writer = new SessionEventWriter(directory))
            {
                for (var index = 0; index < 260; index++)
                {
                    await writer.WriteAsync(new() { OffsetMs = index, Kind = index % 2 == 0 ? "Gift" : "Danmu", Text = "事件" + index });
                }
                await writer.WriteAsync(new() { OffsetMs = 4, Kind = "Gift", Text = "迟到礼物" });
            }
            var reader = new EventArchiveReader();
            var records = new List<RecordedEventEnvelope>();
            long? cursor = null;
            do
            {
                var page = await reader.ReadAsync(directory, 0, 20, "gift", cursor, 3);
                Assert.InRange(page.Items.Count, 1, 3);
                records.AddRange(page.Items);
                cursor = page.NextCursor;
            } while (cursor is not null);
            Assert.Equal(12, records.Count);
            Assert.Equal(records.Count, records.Select(item => item.Sequence).Distinct().Count());
            Assert.Equal(records.Select(item => item.Sequence).Order(), records.Select(item => item.Sequence));
            Assert.All(records, item => Assert.Equal("Gift", item.Kind));
            Assert.Equal("迟到礼物", records[^1].Text);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ReopeningIndexesCommittedCrashTailBeforeAppendingNewBlocks()
    {
        var directory = CreateDirectory();
        try
        {
            await using (var writer = new SessionEventWriter(directory))
            {
                for (var index = 0; index < 128; index++)
                {
                    await writer.WriteAsync(new() { OffsetMs = index });
                }
            }
            for (var sequence = 129; sequence <= 159; sequence++)
            {
                await AppendEventAsync(directory, new() { Sequence = sequence, OffsetMs = 9000, Text = "崩溃尾部" });
            }
            await File.AppendAllTextAsync(EventPath(directory), "{\"sequence\":99999");
            await using (var writer = new SessionEventWriter(directory))
            {
                await writer.WriteAsync(new() { OffsetMs = 20000, Text = "新事件" });
            }
            var reader = new EventArchiveReader();
            var tail = await reader.ReadAsync(directory, 9000, 9000, null, null, 100);
            Assert.Equal(31, tail.Items.Count);
            var latest = await reader.ReadAsync(directory, 20000, 20000, null, null, 100);
            Assert.Equal(160, Assert.Single(latest.Items).Sequence);
            var blocks = await ReadBlocksAsync(directory);
            Assert.Equal([128, 32], blocks.Select(block => block.Count));
            Assert.Equal(blocks[0].EndByte, blocks[1].StartByte);
            Assert.Equal(160, blocks[^1].LastSequence);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task LargeLastEventDoesNotResetTheRecoveredSequence()
    {
        var directory = CreateDirectory();
        try
        {
            await using (var writer = new SessionEventWriter(directory))
            {
                await writer.WriteAsync(new() { OffsetMs = 1 });
            }
            var text = new string('中', 200000);
            await AppendEventAsync(directory, new() { Sequence = 2, OffsetMs = 2, Text = text });
            await File.AppendAllTextAsync(EventPath(directory), "{\"sequence\":100000");
            await using (var writer = new SessionEventWriter(directory))
            {
                await writer.WriteAsync(new() { OffsetMs = 3, Text = "恢复后" });
            }
            var result = await new EventArchiveReader().ReadAsync(directory, 0, 10, null, null, 10);
            Assert.Equal([1L, 2L, 3L], result.Items.Select(item => item.Sequence));
            Assert.Equal(text, result.Items[1].Text);
            Assert.Equal(3, (await ReadBlocksAsync(directory))[^1].LastSequence);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid-json")]
    [InlineData("invalid-range")]
    [InlineData("gap")]
    [InlineData("truncated")]
    public async Task MissingOrDamagedIndexIsRebuiltWithoutOmittingEvents(string damage)
    {
        var directory = CreateDirectory();
        try
        {
            await using (var writer = new SessionEventWriter(directory))
            {
                for (var index = 0; index < 300; index++)
                {
                    await writer.WriteAsync(new() { OffsetMs = index });
                }
            }
            var lines = await File.ReadAllLinesAsync(IndexPath(directory));
            switch (damage)
            {
                case "missing":
                    File.Delete(IndexPath(directory));
                    break;
                case "invalid-json":
                    await File.WriteAllTextAsync(IndexPath(directory), lines[0] + "\n{broken}\n" + lines[^1] + "\n");
                    break;
                case "invalid-range":
                    var invalid = JsonSerializer.Deserialize<EventIndexBlock>(lines[0], JsonFiles.Options)!;
                    invalid.EndByte--;
                    await File.WriteAllTextAsync(IndexPath(directory), JsonSerializer.Serialize(invalid, JsonFiles.Options) + "\n");
                    break;
                case "gap":
                    await File.WriteAllTextAsync(IndexPath(directory), lines[0] + "\n" + lines[^1] + "\n");
                    break;
                case "truncated":
                    await File.WriteAllTextAsync(IndexPath(directory), lines[0] + "\n{\"startByte\":");
                    break;
            }
            var result = await new EventArchiveReader().ReadAsync(directory, 0, 500, null, null, 1000);
            Assert.Equal(300, result.Items.Count);
            Assert.Equal(300, result.Items.Select(item => item.Sequence).Distinct().Count());
            var blocks = await ReadBlocksAsync(directory);
            Assert.Equal([128, 128, 44], blocks.Select(block => block.Count));
            Assert.Equal(new FileInfo(EventPath(directory)).Length, blocks[^1].EndByte);
            Assert.Equal(300, blocks[^1].LastSequence);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ReaderSkipsUnterminatedJsonEvenWhenItIsOtherwiseValid()
    {
        var directory = CreateDirectory();
        try
        {
            await using (var writer = new SessionEventWriter(directory))
            {
                await writer.WriteAsync(new() { OffsetMs = 1, Text = "已提交" });
            }
            await File.AppendAllTextAsync(EventPath(directory), JsonSerializer.Serialize(new RecordedEventEnvelope { Sequence = 2, OffsetMs = 2, Text = "未提交" }, JsonFiles.Options));
            var originalLength = new FileInfo(EventPath(directory)).Length;
            var reader = new EventArchiveReader();
            var result = await reader.ReadAsync(directory, 0, 10, null, null, 10);
            Assert.Equal("已提交", Assert.Single(result.Items).Text);
            var enumerated = new List<RecordedEventEnvelope>();
            await foreach (var item in reader.EnumerateAsync(EventPath(directory)))
            {
                enumerated.Add(item);
            }
            Assert.Single(enumerated);
            Assert.Equal(originalLength, new FileInfo(EventPath(directory)).Length);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task EnumerationUsesOneCommittedSnapshotWhileArchiveGrows()
    {
        var directory = CreateDirectory();
        try
        {
            await using (var writer = new SessionEventWriter(directory))
            {
                await writer.WriteAsync(new() { OffsetMs = 1 });
            }
            await using var enumerator = new EventArchiveReader().EnumerateAsync(EventPath(directory)).GetAsyncEnumerator();
            Assert.True(await enumerator.MoveNextAsync());
            await AppendEventAsync(directory, new() { Sequence = 2, OffsetMs = 2 });
            Assert.False(await enumerator.MoveNextAsync());
            var result = await new EventArchiveReader().ReadAsync(directory, 0, 10, null, null, 10);
            Assert.Equal(2, result.Items.Count);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task LiveReaderUsesUnindexedTailWithoutPublishingOverTheWriter()
    {
        var directory = CreateDirectory();
        try
        {
            await using (var writer = new SessionEventWriter(directory))
            {
                await writer.WriteAsync(new() { OffsetMs = 1, Text = "实时事件" });
                await WaitForLengthAsync(EventPath(directory), 0);
                var result = await new EventArchiveReader().ReadAsync(directory, 0, 10, null, null, 10);
                Assert.Equal("实时事件", Assert.Single(result.Items).Text);
                Assert.Empty(await ReadBlocksAsync(directory));
                await writer.WriteAsync(new() { OffsetMs = 2 });
            }
            var block = Assert.Single(await ReadBlocksAsync(directory));
            Assert.Equal(2, block.Count);
            Assert.Equal(2, block.LastSequence);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task SecondWriterCannotAcquireTheSameArchiveLease()
    {
        var directory = CreateDirectory();
        try
        {
            await using (var writer = new SessionEventWriter(directory))
            {
                await writer.WriteAsync(new() { OffsetMs = 1 });
                await WaitForLengthAsync(EventPath(directory), 0);
                Assert.Throws<IOException>(() => new SessionEventWriter(directory));
                await writer.WriteAsync(new() { OffsetMs = 2 });
            }
            var result = await new EventArchiveReader().ReadAsync(directory, 0, 10, null, null, 10);
            Assert.Equal([1L, 2L], result.Items.Select(item => item.Sequence));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "recorder-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string EventPath(string directory) => Path.Combine(directory, "事件.jsonl");

    private static string IndexPath(string directory) => Path.Combine(directory, "事件.index.jsonl");

    private static Task AppendEventAsync(string directory, RecordedEventEnvelope value) => File.AppendAllTextAsync(EventPath(directory), JsonSerializer.Serialize(value, JsonFiles.Options) + "\n", new UTF8Encoding(false));

    private static async Task<List<EventIndexBlock>> ReadBlocksAsync(string directory)
    {
        await using var stream = new FileStream(IndexPath(directory), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var result = new List<EventIndexBlock>();
        while (await reader.ReadLineAsync() is { } line)
        {
            result.Add(JsonSerializer.Deserialize<EventIndexBlock>(line, JsonFiles.Options)!);
        }
        return result;
    }

    private static async Task WaitForLengthAsync(string path, long previousLength)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (new FileInfo(path).Length <= previousLength)
        {
            await Task.Delay(5, timeout.Token);
        }
    }
}
