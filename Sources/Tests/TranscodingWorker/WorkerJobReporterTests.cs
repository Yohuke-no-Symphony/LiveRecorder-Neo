using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;
using Yohuke.LiveRecorder.Transcoding.Worker.Scheduling;
using Xunit;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Tests;

/// <summary>验证阶段指标、周期日志和 FFmpeg 进度解析。</summary>
public sealed class WorkerJobReporterTests
{
    /// <summary>下载的阶段百分比、总体进度、速率和预计时间使用各自单位。</summary>
    [Fact]
    public void TransferProgressKeepsStageMetricsSeparateFromOverallProgress()
    {
        using var job = new WorkerRunningJob(new() { JobId = "job", Attempt = 2 }, CancellationToken.None);
        var logs = new List<WorkerLogEntry>();
        var time = new WorkerTestTimeProvider();
        var reporter = new WorkerJobReporter(job, logs.Add, time);
        reporter.Start("Downloading", 0, "Downloading source");
        reporter.Transfer(new(100, 1000, "source.flv", 0, 1));
        time.Elapsed = TimeSpan.FromSeconds(2);
        reporter.Transfer(new(300, 1000, "source.flv", 0, 1));
        reporter.Pulse();
        var progress = job.Snapshot();
        Assert.Equal(3, progress.Progress);
        Assert.Equal(300, progress.Activity!.BytesTransferred);
        Assert.Equal(100, progress.Activity.BytesPerSecond);
        Assert.Equal(7, progress.Activity.EstimatedSecondsRemaining);
        Assert.Contains("30.0%", logs[^1].Message);
        Assert.Equal("job", logs[^1].JobId);
        Assert.Equal(2, logs[^1].Attempt);
        time.Elapsed = TimeSpan.FromSeconds(7);
        reporter.Pulse();
        Assert.Equal(0, job.Snapshot().Activity!.BytesPerSecond);
        Assert.Null(job.Snapshot().Activity!.EstimatedSecondsRemaining);
    }

    /// <summary>阶段切换清空传输指标，迟到传输回调不会覆盖编码阶段。</summary>
    [Fact]
    public void StageTransitionsClearMetricsAndRejectLateTransferCallbacks()
    {
        using var job = new WorkerRunningJob(new(), CancellationToken.None);
        var time = new WorkerTestTimeProvider();
        var reporter = new WorkerJobReporter(job, _ => { }, time);
        reporter.Start("Downloading", 0, "Downloading");
        reporter.Transfer(new(100, 1000, "source.flv", 0, 1));
        reporter.Start("Inspecting", 10, "Inspecting");
        reporter.Transfer(new(900, 1000, "source.flv", 0, 1));
        Assert.Equal("Inspecting", job.Snapshot().Stage);
        Assert.Null(job.Snapshot().Activity);
        reporter.Start("Encoding", 15, "Encoding", "h264_videotoolbox");
        reporter.Encoding(new(30000, 40, 2), 60000);
        var progress = job.Snapshot();
        Assert.Equal(47.5, progress.Progress);
        Assert.Equal(15, progress.Activity!.EstimatedSecondsRemaining);
        Assert.Equal("h264_videotoolbox", progress.Encoder);
        time.Elapsed = TimeSpan.FromSeconds(6);
        reporter.Pulse();
        Assert.Equal(0, job.Snapshot().Activity!.Speed);
        reporter.Start("Finalizing", 99, "Finalizing");
        Assert.Null(job.Snapshot().Activity);
    }

    /// <summary>FFmpeg 的成组输出在 progress 边界上产生完整快照，非法数字省略。</summary>
    [Fact]
    public void EncodingParserPublishesGroupedFiniteMetrics()
    {
        var values = new List<WorkerEncodingProgress>();
        var parser = new WorkerEncodingProgressParser(values.Add);
        parser.Add("fps=50.5");
        parser.Add("out_time_us=12000000");
        parser.Add("speed=2.0x");
        Assert.Empty(values);
        parser.Add("progress=continue");
        Assert.Equal(new(12000, 50.5, 2), Assert.Single(values));
        parser.Add("fps=NaN");
        parser.Add("speed=Infinityx");
        parser.Add("progress=end");
        Assert.Null(values[^1].FramesPerSecond);
        Assert.Null(values[^1].Speed);
        Assert.Equal(12000, values[^1].ProcessedDurationMs);
    }

    /// <summary>终态后停止采样，耗时清理或结果上报期间保持终态日志顺序。</summary>
    [Fact]
    public void StoppedReporterDoesNotAppendActivityAfterTheFinalMessage()
    {
        using var job = new WorkerRunningJob(new(), CancellationToken.None);
        var messages = new List<WorkerLogEntry>();
        var reporter = new WorkerJobReporter(job, messages.Add);
        reporter.Start("Downloading", 0, "Downloading");
        reporter.Stop();
        reporter.Message("Stopped job.", "Warning");
        reporter.Transfer(new(100, 100, "source.flv", 1, 1));
        reporter.Encoding(new(1000, 10, 1), 2000);
        reporter.Pulse();
        Assert.Equal(2, messages.Count);
        Assert.Equal("Stopped job.", messages[^1].Message);
        Assert.Null(job.Snapshot().Activity);
    }
}
