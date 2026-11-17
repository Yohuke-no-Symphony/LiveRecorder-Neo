using System.Text.Json;
using Xunit;
using Yohuke.LiveRecorder.Clipping.Contracts;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证步骤身份、旧协议兼容和可续传内容的规范化指纹。</summary>
public sealed class WorkerStagePolicyTests
{
    /// <summary>旧节点省略恢复与步骤字段时保留空值和原协议版本。</summary>
    [Fact]
    public void LegacyMessagesKeepRecoveryOptional()
    {
        var heartbeat = JsonSerializer.Deserialize<WorkerHeartbeat>("{}", WorkerProtocol.JsonOptions)!;
        Assert.Empty(heartbeat.RecoveryRequests);
        Assert.Empty(heartbeat.Jobs);
        Assert.Equal(0, new WorkerWelcome().RecoveryProtocolVersion);
        Assert.Equal(0, new TranscodeAssignment().CheckpointGeneration);
        Assert.Equal(0, new ClipAssignment().CheckpointGeneration);
    }

    /// <summary>默认流水线顺序固定，重复步骤不能冒充独立的执行快照。</summary>
    [Theory]
    [InlineData("Transcoding", "Inspecting", "Encoding")]
    [InlineData("Clipping", "Preparing", "Encoding")]
    public void DefaultPipelinesNormalizeValuesAndRejectDuplicateStageIds(string kind, string second, string third)
    {
        var stages = WorkerStageSnapshotPolicy.Create(kind);
        Assert.Equal(new[] { "Downloading", second, third, "Validating", "Uploading", "Finalizing" }, stages.Select(stage => stage.Id));
        stages[0].State = "Completed";
        stages[1].State = "Interrupted";
        stages[1].Progress = double.NaN;
        stages[1].ElapsedMs = -1;
        var normalized = WorkerStageSnapshotPolicy.Normalize(kind, stages);
        Assert.Equal("Completed", normalized[0].State);
        Assert.Equal("Interrupted", normalized[1].State);
        Assert.Equal(0, normalized[1].Progress);
        Assert.Equal(0, normalized[1].ElapsedMs);
        stages[1].Id = "Downloading";
        Assert.Empty(WorkerStageSnapshotPolicy.Normalize(kind, stages));
    }

    /// <summary>扩展步骤保留节点上报顺序，不需要服务端同步硬编码新的流水线定义。</summary>
    [Fact]
    public void ExtendedStagesKeepTheirIdentityAndReportedOrder()
    {
        var stages = WorkerStageSnapshotPolicy.Create("Transcoding");
        stages.Insert(3, new() { Id = "Custom-Packaging", State = "Running", Progress = 25 });
        var normalized = WorkerStageSnapshotPolicy.Normalize("Transcoding", stages);
        Assert.Equal(stages.Select(stage => stage.Id), normalized.Select(stage => stage.Id));
        Assert.Equal("Custom-Packaging", normalized[3].Id);
        Assert.Equal(25, normalized[3].Progress);
        Assert.Equal("Running", normalized[3].State);
        Assert.All(WorkerStageSnapshotPolicy.Complete("Transcoding", stages), stage => Assert.Equal("Completed", stage.State));
    }

    /// <summary>空旧快照保持兼容，超量或非法阶段标识不会进入任务监控存储。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("bad stage")]
    [InlineData("../escape")]
    [InlineData("bad\nline")]
    public void InvalidStageIdentitiesAreRejected(string id)
    {
        Assert.Empty(WorkerStageSnapshotPolicy.Normalize("Transcoding", [new() { Id = id }]));
        Assert.Empty(WorkerStageSnapshotPolicy.Normalize("Transcoding", []));
        Assert.Empty(WorkerStageSnapshotPolicy.Normalize("Transcoding", Enumerable.Range(0, 33)
            .Select(index => new WorkerStageSnapshot { Id = "Stage" + index }).ToArray()));
    }

    /// <summary>相同转码文件清单不受枚举顺序和校验值大小写影响，内容变化必须开启新上传会话。</summary>
    [Fact]
    public void TranscodeFingerprintIsCanonicalAndContentBound()
    {
        var first = new TranscodeOutputManifest
        {
            Files = [new() { Name = "index.m3u8", Length = 10, Sha256 = new('A', 64) }, new() { Name = "init.mp4", Length = 20, Sha256 = new('B', 64) }]
        };
        var reordered = new TranscodeOutputManifest
        {
            Files = first.Files.AsEnumerable().Reverse().Select(file => file with { Sha256 = file.Sha256.ToLowerInvariant() }).ToList()
        };
        Assert.Equal(WorkerOutputManifestFingerprint.Create(first), WorkerOutputManifestFingerprint.Create(reordered));
        reordered.Files[0] = reordered.Files[0] with { Length = 21 };
        Assert.NotEqual(WorkerOutputManifestFingerprint.Create(first), WorkerOutputManifestFingerprint.Create(reordered));
    }

    /// <summary>切片内容相同但时间映射发生变化时不能继续复用已上传字节。</summary>
    [Fact]
    public void ClipFingerprintIncludesTheActualTimeBounds()
    {
        var manifest = new ClipOutputManifest { Bytes = 10, Sha256 = new('a', 64), ActualStartMs = 100, ActualEndMs = 200 };
        Assert.NotEqual(WorkerOutputManifestFingerprint.Create(manifest),
            WorkerOutputManifestFingerprint.Create(manifest with { ActualEndMs = 201 }));
    }
}
