using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

/// <summary>按场次顺序追加事件，每一百二十八条提交一个可重建索引块。</summary>
public sealed class SessionEventWriter : IAsyncDisposable
{
    private readonly Channel<RecordedEventEnvelope> queue = Channel.CreateBounded<RecordedEventEnvelope>(new BoundedChannelOptions(4096)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly Task writerTask;
    private readonly Func<EventArchiveCommit, Task>? onCommit;
    private long sequence;

    /// <summary>持有场次写租约，修复未提交尾行和索引后开始追加。</summary>
    public SessionEventWriter(string directory, Func<EventArchiveCommit, Task>? onCommit = null)
    {
        this.onCommit = onCommit;
        Directory.CreateDirectory(directory);
        var lease = EventArchiveReader.AcquireLease(directory);
        FileStream? output = null;
        FileStream? index = null;
        try
        {
            var indexPath = Path.Combine(directory, "事件.index.jsonl");
            output = new(Path.Combine(directory, "事件.jsonl"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, 65536, true);
            var recovered = EventArchiveReader.RecoverWriter(output, indexPath);
            sequence = recovered.LastSequence;
            index = new(indexPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, true);
            writerTask = RunAsync(output, index, lease, recovered.Block);
        }
        catch
        {
            index?.Dispose();
            output?.Dispose();
            lease.Dispose();
            throw;
        }
    }

    /// <summary>排队记录事件，失败或积压超过期限时向调用方报告。</summary>
    public async Task WriteAsync(RecordedEventEnvelope message, CancellationToken cancellationToken = default)
    {
        if (writerTask.IsFaulted)
        {
            await writerTask;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await queue.Writer.WriteAsync(message, deadline.Token);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        queue.Writer.TryComplete();
        await writerTask;
    }

    private async Task RunAsync(FileStream outputStream, FileStream indexStream, FileStream leaseStream, EventIndexBlock block)
    {
        await using var lease = leaseStream;
        await using var output = outputStream;
        await using var index = indexStream;
        try
        {
            await foreach (var message in queue.Reader.ReadAllAsync())
            {
                message.Sequence = checked(++sequence);
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, EventArchiveJson.Options) + "\n");
                await output.WriteAsync(bytes);
                EventArchiveReader.Include(block, message, output.Position);
                if (block.Count == EventIndexBlock.MAX_EVENTS)
                {
                    await output.FlushAsync();
                    await WriteIndexAsync(index, block);
                    block = EventArchiveReader.CreateBlock(output.Position, sequence);
                    await ReportCommitAsync(output, index, block, false);
                }
                else if (queue.Reader.Count == 0)
                {
                    await output.FlushAsync();
                    await ReportCommitAsync(output, index, block, false);
                }
            }
            await output.FlushAsync();
            if (block.Count > 0)
            {
                await WriteIndexAsync(index, block);
                block = EventArchiveReader.CreateBlock(output.Position, sequence);
            }
            await ReportCommitAsync(output, index, block, true);
        }
        catch (Exception exception)
        {
            queue.Writer.TryComplete(exception);
            throw;
        }
    }

    private async Task ReportCommitAsync(FileStream output, FileStream index, EventIndexBlock block, bool closed)
    {
        if (onCommit is null)
        {
            return;
        }
        await output.FlushAsync();
        await index.FlushAsync();
        output.Flush(true);
        index.Flush(true);
        await onCommit(new()
        {
            CommittedBytes = output.Position,
            IndexCommittedBytes = index.Position,
            ThroughSequence = sequence,
            IsClosed = closed,
            PendingBlock = block.Count == 0 ? null : new()
            {
                MetadataVersion = block.MetadataVersion,
                StartByte = block.StartByte,
                EndByte = block.EndByte,
                MinMs = block.MinMs,
                MaxMs = block.MaxMs,
                Count = block.Count,
                LastSequence = block.LastSequence,
                Kinds = [.. block.Kinds],
                RedPocketKeys = [.. block.RedPocketKeys]
            }
        });
    }

    private static async Task WriteIndexAsync(FileStream index, EventIndexBlock block)
    {
        await index.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(block, EventArchiveJson.Options) + "\n"));
        await index.FlushAsync();
    }
}
