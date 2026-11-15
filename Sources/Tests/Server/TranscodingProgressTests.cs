using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证任务阶段与诊断指标的旧数据兼容、有效范围和阶段隔离。</summary>
public sealed class TranscodingProgressTests
{
    /// <summary>新增诊断字段在旧任务和省略字段的心跳中保持可选。</summary>
    [Fact]
    public void ExistingJobsAndHeartbeatsDoNotRequireActivity()
    {
        Assert.Null(new TranscodeJob().Activity);
        Assert.Null(new TranscodeJob().LastProgressAtUtc);
        Assert.Null(new WorkerJobProgress().Activity);
        Assert.Null(TranscodeJobProgressPolicy.NormalizeActivity("Downloading", null));
    }

    /// <summary>已知新阶段可持久化，未知阶段归入运行状态。</summary>
    [Theory]
    [InlineData("Inspecting", "Inspecting")]
    [InlineData("Finalizing", "Finalizing")]
    [InlineData("WaitingForGpu", "WaitingForGpu")]
    [InlineData("Encoding", "Encoding")]
    [InlineData("future-stage", "Running")]
    public void StageNormalizationSupportsNewAndUnknownWorkers(string stage, string expected)
    {
        Assert.Equal(expected, TranscodeJobProgressPolicy.NormalizeStage(stage));
    }

    /// <summary>传输指标保留完成量与文件信息，同时清除编码指标。</summary>
    [Theory]
    [InlineData("Downloading")]
    [InlineData("Uploading")]
    public void TransferActivityOnlyContainsTransferMeasurements(string stage)
    {
        var activity = TranscodeJobProgressPolicy.NormalizeActivity(stage, new()
        {
            BytesTransferred = 50, TotalBytes = 100, BytesPerSecond = 10, EstimatedSecondsRemaining = 5,
            FilesCompleted = 1, FilesTotal = 3, CurrentFile = "chunk_00001.m4s",
            FramesPerSecond = 90, Speed = 3, ProcessedDurationMs = 1000, TotalDurationMs = 2000
        });
        Assert.NotNull(activity);
        Assert.Equal(50, activity.BytesTransferred);
        Assert.Equal(100, activity.TotalBytes);
        Assert.Equal(10, activity.BytesPerSecond);
        Assert.Equal(5, activity.EstimatedSecondsRemaining);
        Assert.Equal("chunk_00001.m4s", activity.CurrentFile);
        Assert.Equal(1, activity.FilesCompleted);
        Assert.Equal(3, activity.FilesTotal);
        Assert.Null(activity.FramesPerSecond);
        Assert.Null(activity.ProcessedDurationMs);
        Assert.Null(activity.Speed);
    }

    /// <summary>编码指标清除旧传输数据，帧尾时间误差被限制到总时长。</summary>
    [Fact]
    public void EncodingActivityOnlyContainsEncodingMeasurements()
    {
        var activity = TranscodeJobProgressPolicy.NormalizeActivity("Encoding", new()
        {
            ProcessedDurationMs = 2001, TotalDurationMs = 2000, FramesPerSecond = 90, Speed = 3,
            EstimatedSecondsRemaining = 0, BytesTransferred = 50, TotalBytes = 100, CurrentFile = "old-input"
        });
        Assert.NotNull(activity);
        Assert.Equal(2000, activity.ProcessedDurationMs);
        Assert.Equal(2000, activity.TotalDurationMs);
        Assert.Equal(90, activity.FramesPerSecond);
        Assert.Equal(3, activity.Speed);
        Assert.Null(activity.BytesTransferred);
        Assert.Null(activity.CurrentFile);
    }

    /// <summary>无法提供量化进度的阶段不保留前一阶段的指标。</summary>
    [Theory]
    [InlineData("Inspecting")]
    [InlineData("WaitingForGpu")]
    [InlineData("Validating")]
    [InlineData("Finalizing")]
    [InlineData("Running")]
    public void StageChangesClearThePreviousMeasurements(string stage)
    {
        Assert.Null(TranscodeJobProgressPolicy.NormalizeActivity(stage, new() { BytesTransferred = 50, TotalBytes = 100 }));
    }

    /// <summary>非法诊断数据按空值处理，续租流程可以继续。</summary>
    [Fact]
    public void InvalidActivityIsDropped()
    {
        Assert.Null(TranscodeJobProgressPolicy.NormalizeActivity("Downloading", new() { BytesTransferred = 101, TotalBytes = 100 }));
        Assert.Null(TranscodeJobProgressPolicy.NormalizeActivity("Downloading", new() { BytesTransferred = -1 }));
        Assert.Null(TranscodeJobProgressPolicy.NormalizeActivity("Uploading", new() { FilesCompleted = 4, FilesTotal = 3 }));
        Assert.Null(TranscodeJobProgressPolicy.NormalizeActivity("Uploading", new() { FilesTotal = 100001 }));
        Assert.Null(TranscodeJobProgressPolicy.NormalizeActivity("Uploading", new() { CurrentFile = "bad\nfile" }));
        Assert.Null(TranscodeJobProgressPolicy.NormalizeActivity("Encoding", new() { ProcessedDurationMs = double.NaN }));
        Assert.Null(TranscodeJobProgressPolicy.NormalizeActivity("Encoding", new() { Speed = double.PositiveInfinity }));
        Assert.Null(TranscodeJobProgressPolicy.NormalizeActivity("Encoding", new() { EstimatedSecondsRemaining = -1 }));
    }
}
