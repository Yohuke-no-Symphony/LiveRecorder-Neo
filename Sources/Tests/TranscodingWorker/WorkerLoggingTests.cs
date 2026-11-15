using System.Text.Json;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;
using Xunit;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Tests;

/// <summary>验证有界日志队列、精确批次确认及旧协议兼容。</summary>
public sealed class WorkerLoggingTests
{
    /// <summary>未确认批次保持原内容，错误或超前确认不能丢弃待发日志。</summary>
    [Fact]
    public void InFlightBatchIsFrozenUntilItsExactAcknowledgement()
    {
        var buffer = new WorkerLogBuffer();
        buffer.Append(new() { Message = "first" });
        var batch = Assert.IsType<WorkerLogBatch>(buffer.GetBatch());
        buffer.Append(new() { Message = "second" });
        Assert.Same(batch, buffer.GetBatch());
        Assert.Single(batch.Entries);
        buffer.Acknowledge(new() { StreamId = "another-stream", BatchId = batch.BatchId, ThroughSequence = 1 });
        buffer.Acknowledge(new() { StreamId = batch.StreamId, BatchId = "another-batch", ThroughSequence = 1 });
        buffer.Acknowledge(new() { StreamId = batch.StreamId, BatchId = batch.BatchId, ThroughSequence = 2 });
        Assert.Same(batch, buffer.GetBatch());
        buffer.Acknowledge(Acknowledgement(batch));
        var next = Assert.IsType<WorkerLogBatch>(buffer.GetBatch());
        Assert.NotEqual(batch.BatchId, next.BatchId);
        Assert.Equal("second", Assert.Single(next.Entries).Message);
        Assert.Equal(2, next.Entries[0].Sequence);
        buffer.Acknowledge(Acknowledgement(next));
        Assert.Null(buffer.GetBatch());
    }

    /// <summary>断线期间队列有界，并上报被淘汰的记录数量。</summary>
    [Fact]
    public void OverflowPreservesInFlightRecordsAndReportsDroppedEntries()
    {
        var buffer = new WorkerLogBuffer();
        for (var index = 0; index < 1100; index++)
        {
            buffer.Append(new() { Message = new('x', 3000) });
        }
        var batch = Assert.IsType<WorkerLogBatch>(buffer.GetBatch());
        Assert.Equal(100, batch.DroppedCount);
        Assert.Equal(64, batch.Entries.Count);
        Assert.Equal(101, batch.Entries[0].Sequence);
        Assert.All(batch.Entries, entry => Assert.Equal(2048, entry.Message.Length));
        for (var index = 0; index < 1100; index++)
        {
            buffer.Append(new() { Message = "new" });
        }
        Assert.Same(batch, buffer.GetBatch());
        buffer.Acknowledge(Acknowledgement(batch));
        var next = Assert.IsType<WorkerLogBatch>(buffer.GetBatch());
        Assert.True(next.DroppedCount > 0);
        Assert.Equal(1201, next.Entries[0].Sequence);
    }

    /// <summary>并发任务产生的日志获得唯一递增序号。</summary>
    [Fact]
    public void ConcurrentWritersKeepUniqueOrderedSequences()
    {
        var buffer = new WorkerLogBuffer();
        Parallel.For(0, 400, index => buffer.Append(new() { Message = index.ToString() }));
        var sequences = new List<long>();
        while (buffer.GetBatch() is { } batch)
        {
            sequences.AddRange(batch.Entries.Select(entry => entry.Sequence));
            buffer.Acknowledge(Acknowledgement(batch));
        }
        Assert.Equal(Enumerable.Range(1, 400).Select(value => (long)value), sequences);
    }

    /// <summary>旧主站和节点省略新字段时保持原有配置和心跳行为。</summary>
    [Fact]
    public void MissingLogAndActivityFieldsRemainCompatibleWithOlderPeers()
    {
        Assert.Equal(0, JsonSerializer.Deserialize<WorkerWelcome>("{}", WorkerProtocol.JsonOptions)!.LogProtocolVersion);
        Assert.Null(JsonSerializer.Deserialize<WorkerHeartbeat>("{}", WorkerProtocol.JsonOptions)!.LogBatch);
        Assert.Null(JsonSerializer.Deserialize<WorkerState>("{}", WorkerProtocol.JsonOptions)!.LogAcknowledgement);
        Assert.Null(JsonSerializer.Deserialize<WorkerJobProgress>("{}", WorkerProtocol.JsonOptions)!.Activity);
    }

    private static WorkerLogAcknowledgement Acknowledgement(WorkerLogBatch batch) => new()
    {
        StreamId = batch.StreamId, BatchId = batch.BatchId, ThroughSequence = batch.Entries[^1].Sequence
    };
}
