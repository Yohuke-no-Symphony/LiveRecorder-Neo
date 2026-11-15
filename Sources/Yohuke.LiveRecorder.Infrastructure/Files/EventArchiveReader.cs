using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

/// <summary>利用可重建侧索引查询已提交的事件文件快照。</summary>
public sealed class EventArchiveReader
{
    private const int BUFFER_BYTES = 65536;

    /// <summary>按归档顺序分页读取时间范围，索引损坏时补建完整记录的索引。</summary>
    public Task<EventPage> ReadAsync(string directory, double fromMs, double toMs, string? kind, long? cursor, int limit, CancellationToken cancellationToken = default)
    {
        return ReadAsync(directory, fromMs, toMs, kind, cursor, limit, null, cancellationToken);
    }

    /// <summary>在分页计数前应用正文正则，保持归档游标及完整事件内容。</summary>
    public async Task<EventPage> ReadAsync(string directory, double fromMs, double toMs, string? kind, long? cursor, int limit, Regex? textPattern, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(directory, "事件.jsonl");
        await using var stream = OpenRead(path);
        if (stream is null)
        {
            return new();
        }
        var snapshotLength = stream.Length;
        var committedLength = CommittedLength(stream, snapshotLength);
        var indexPath = Path.Combine(directory, "事件.index.jsonl");
        var (blocks, needsRepair) = LoadIndex(stream, indexPath, committedLength, cancellationToken);
        if (needsRepair)
        {
            RemovePartialBlock(blocks);
            var block = CreateBlock(blocks.LastOrDefault()?.EndByte ?? 0, blocks.LastOrDefault()?.LastSequence ?? 0);
            await foreach (var line in CompleteLinesAsync(stream, block.StartByte, committedLength, cancellationToken))
            {
                block.EndByte = line.EndByte;
                if (DeserializeEvent(line.Bytes) is not { } item)
                {
                    continue;
                }
                Include(block, item, line.EndByte);
                if (block.Count == EventIndexBlock.MAX_EVENTS)
                {
                    blocks.Add(block);
                    block = CreateBlock(block.EndByte, block.LastSequence);
                }
            }
            if (block.Count > 0)
            {
                blocks.Add(block);
            }
            TryPublishIndex(directory, indexPath, snapshotLength, blocks);
        }
        limit = Math.Clamp(limit, 1, 1000);
        var items = new List<RecordedEventEnvelope>(limit + 1);
        foreach (var block in blocks)
        {
            if (block.MaxMs < fromMs || block.MinMs > toMs || block.LastSequence <= (cursor ?? 0))
            {
                continue;
            }
            await foreach (var line in CompleteLinesAsync(stream, block.StartByte, block.EndByte, cancellationToken))
            {
                if (DeserializeEvent(line.Bytes) is not { } item ||
                    item.OffsetMs < fromMs || item.OffsetMs > toMs || item.Sequence <= (cursor ?? 0) ||
                    !string.IsNullOrEmpty(kind) && !string.Equals(kind, "All", StringComparison.OrdinalIgnoreCase) && !string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase) ||
                    textPattern is not null && !textPattern.IsMatch(item.Text))
                {
                    continue;
                }
                items.Add(item);
                if (items.Count > limit)
                {
                    break;
                }
            }
            if (items.Count > limit)
            {
                break;
            }
        }
        var page = items.Take(limit).ToList();
        return new() { Items = page, NextCursor = items.Count > limit ? page[^1].Sequence : null };
    }

    /// <summary>枚举调用开始时已完整换行的记录，后续追加内容留给下一次读取。</summary>
    public IAsyncEnumerable<RecordedEventEnvelope> EnumerateAsync(string path, CancellationToken cancellationToken = default)
    {
        return EnumerateAsync(path, long.MaxValue, cancellationToken);
    }

    internal async IAsyncEnumerable<RecordedEventEnvelope> EnumerateAsync(string path, long maximumLength,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var stream = OpenRead(path);
        if (stream is null)
        {
            yield break;
        }
        var length = CommittedLength(stream, maximumLength);
        await foreach (var line in CompleteLinesAsync(stream, 0, length, cancellationToken))
        {
            if (DeserializeEvent(line.Bytes) is { } item)
            {
                yield return item;
            }
        }
    }

    internal static FileStream AcquireLease(string directory) =>
        new(Path.Combine(directory, ".事件.writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    internal static (EventIndexBlock Block, long LastSequence) RecoverWriter(FileStream stream, string indexPath)
    {
        var length = CommittedLength(stream, stream.Length);
        stream.SetLength(length);
        var (blocks, _) = LoadIndex(stream, indexPath, length, CancellationToken.None);
        RemovePartialBlock(blocks);
        var sequence = blocks.LastOrDefault()?.LastSequence ?? 0;
        var block = CreateBlock(blocks.LastOrDefault()?.EndByte ?? 0, sequence);
        foreach (var line in CompleteLines(stream, block.StartByte, length, CancellationToken.None))
        {
            block.EndByte = line.EndByte;
            if (DeserializeEvent(line.Bytes) is not { } item)
            {
                continue;
            }
            sequence = Math.Max(sequence, item.Sequence);
            Include(block, item, line.EndByte);
            if (block.Count == EventIndexBlock.MAX_EVENTS)
            {
                blocks.Add(block);
                block = CreateBlock(block.EndByte, sequence);
            }
        }
        PublishIndex(indexPath, blocks);
        stream.Position = length;
        return (block, sequence);
    }

    internal static EventIndexBlock CreateBlock(long startByte, long lastSequence) => new()
    {
        StartByte = startByte,
        EndByte = startByte,
        MinMs = double.MaxValue,
        MaxMs = double.MinValue,
        LastSequence = lastSequence
    };

    internal static void Include(EventIndexBlock block, RecordedEventEnvelope item, long endByte)
    {
        block.EndByte = endByte;
        block.MinMs = Math.Min(block.MinMs, item.OffsetMs);
        block.MaxMs = Math.Max(block.MaxMs, item.OffsetMs);
        block.LastSequence = Math.Max(block.LastSequence, item.Sequence);
        block.Count++;
    }

    private static (List<EventIndexBlock> Blocks, bool NeedsRepair) LoadIndex(FileStream events, string indexPath, long committedLength, CancellationToken cancellationToken)
    {
        using var index = OpenRead(indexPath);
        if (index is null)
        {
            return ([], true);
        }
        var length = index.Length;
        var committedIndexLength = CommittedLength(index, length);
        var damaged = length != committedIndexLength;
        var blocks = new List<EventIndexBlock>();
        var endByte = 0L;
        var lastSequence = 0L;
        foreach (var line in CompleteLines(index, 0, committedIndexLength, cancellationToken))
        {
            EventIndexBlock? block;
            try
            {
                block = JsonSerializer.Deserialize<EventIndexBlock>(line.Bytes, JsonFiles.Options);
            }
            catch (JsonException)
            {
                damaged = true;
                break;
            }
            if (block is null ||
                block.StartByte != endByte || block.EndByte <= block.StartByte || block.EndByte > committedLength ||
                block.Count is < 1 or > EventIndexBlock.MAX_EVENTS || block.LastSequence <= lastSequence ||
                block.LastSequence - lastSequence < block.Count ||
                !double.IsFinite(block.MinMs) || !double.IsFinite(block.MaxMs) || block.MinMs > block.MaxMs ||
                blocks.LastOrDefault() is { Count: < EventIndexBlock.MAX_EVENTS })
            {
                damaged = true;
                break;
            }
            events.Position = block.EndByte - 1;
            if (events.ReadByte() != '\n')
            {
                damaged = true;
                break;
            }
            blocks.Add(block);
            endByte = block.EndByte;
            lastSequence = block.LastSequence;
        }
        return (blocks, damaged || endByte != committedLength);
    }

    private static void RemovePartialBlock(List<EventIndexBlock> blocks)
    {
        if (blocks.LastOrDefault() is { Count: < EventIndexBlock.MAX_EVENTS })
        {
            blocks.RemoveAt(blocks.Count - 1);
        }
    }

    private static void TryPublishIndex(string directory, string indexPath, long snapshotLength, List<EventIndexBlock> blocks)
    {
        try
        {
            using var lease = AcquireLease(directory);
            using var current = OpenRead(Path.Combine(directory, "事件.jsonl"));
            if (current?.Length == snapshotLength)
            {
                PublishIndex(indexPath, blocks);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void PublishIndex(string path, IEnumerable<EventIndexBlock> blocks)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                foreach (var block in blocks)
                {
                    output.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(block, JsonFiles.Options) + "\n"));
                }
                output.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static FileStream? OpenRead(string path)
    {
        try
        {
            return new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BUFFER_BYTES, true);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static RecordedEventEnvelope? DeserializeEvent(byte[] bytes)
    {
        try
        {
            var item = JsonSerializer.Deserialize<RecordedEventEnvelope>(bytes, JsonFiles.Options);
            return item is { Sequence: > 0 } && double.IsFinite(item.OffsetMs) ? item : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long CommittedLength(FileStream stream, long maximumLength)
    {
        var buffer = new byte[BUFFER_BYTES];
        var end = Math.Min(stream.Length, maximumLength);
        while (end > 0)
        {
            var start = Math.Max(0, end - buffer.Length);
            stream.Position = start;
            var count = 0;
            while (count < end - start)
            {
                var read = stream.Read(buffer, count, (int)(end - start - count));
                if (read == 0)
                {
                    break;
                }
                count += read;
            }
            var newline = count > 0 ? Array.LastIndexOf(buffer, (byte)'\n', count - 1, count) : -1;
            if (newline >= 0)
            {
                return start + newline + 1;
            }
            end = start;
        }
        return 0;
    }

    private static IEnumerable<(long EndByte, byte[] Bytes)> CompleteLines(FileStream stream, long start, long end, CancellationToken cancellationToken)
    {
        stream.Position = start;
        var buffer = new byte[BUFFER_BYTES];
        using var line = new MemoryStream();
        var position = start;
        while (position < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, end - position));
            if (read == 0)
            {
                yield break;
            }
            var fragment = 0;
            for (var offset = 0; offset < read; offset++)
            {
                if (buffer[offset] != '\n')
                {
                    continue;
                }
                line.Write(buffer, fragment, offset - fragment);
                yield return (position + offset + 1, line.ToArray());
                line.SetLength(0);
                fragment = offset + 1;
            }
            line.Write(buffer, fragment, read - fragment);
            position += read;
        }
    }

    private static async IAsyncEnumerable<(long EndByte, byte[] Bytes)> CompleteLinesAsync(FileStream stream, long start, long end, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        stream.Position = start;
        var buffer = new byte[BUFFER_BYTES];
        using var line = new MemoryStream();
        var position = start;
        while (position < end)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, end - position)), cancellationToken);
            if (read == 0)
            {
                yield break;
            }
            var fragment = 0;
            for (var offset = 0; offset < read; offset++)
            {
                if (buffer[offset] != '\n')
                {
                    continue;
                }
                line.Write(buffer, fragment, offset - fragment);
                yield return (position + offset + 1, line.ToArray());
                line.SetLength(0);
                fragment = offset + 1;
            }
            line.Write(buffer, fragment, read - fragment);
            position += read;
        }
    }
}
