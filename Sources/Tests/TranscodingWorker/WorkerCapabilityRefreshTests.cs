using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Scheduling;
using Xunit;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Tests;

/// <summary>验证设备恢复探测不会与执行槽竞争或覆盖较新的配置。</summary>
public sealed class WorkerCapabilityRefreshTests
{
    /// <summary>不可用 GPU 冷却后重新探测，失败设备重新上线后恢复 GPU 优先。</summary>
    [Fact]
    public async Task MissingGpuRecoversAfterCooldownAndFailureInvalidatesCapability()
    {
        var directory = Path.Combine(Path.GetTempPath(), "worker-capability-test-" + Guid.NewGuid().ToString("N"));
        var clock = new WorkerTestTimeProvider();
        var admission = new EncodingAdmission();
        var probes = 0;
        var manager = new WorkerConfigurationManager(new(), configuration => admission.TryAcquireProbe(configuration),
            (_, _) => Task.FromResult(new WorkerCapabilities
            {
                Encoders = [new() { Encoder = "libx264", Available = true }, new() { Encoder = "h264_nvenc", Available = ++probes > 1 }]
            }), clock);
        try
        {
            manager.Request(new() { TempDirectory = directory }, 1, CancellationToken.None);
            await WaitUntilAsync(() => manager.Current.Revision == 1 && manager.Error is null);
            Assert.Equal(1, probes);
            clock.Elapsed = TimeSpan.FromSeconds(59);
            manager.RefreshCapabilitiesWhenDue(CancellationToken.None);
            Assert.Null(manager.Error);
            clock.Elapsed = TimeSpan.FromSeconds(60);
            manager.RefreshCapabilitiesWhenDue(CancellationToken.None);
            await WaitUntilAsync(() => probes == 2 && manager.Error is null);
            Assert.True(manager.Current.Capabilities.Encoders.Single(item => item.Encoder == "h264_nvenc").Available);
            Assert.Equal((0, 0), admission.Running);
            manager.InvalidateEncoder(new() { Encoder = "h264_nvenc" });
            Assert.False(manager.Current.Capabilities.Encoders.Single(item => item.Encoder == "h264_nvenc").Available);
        }
        finally
        {
            await manager.StopAsync();
            Directory.Delete(directory, true);
        }
    }

    /// <summary>探测和实际编码使用同一原子槽，且不突破总并发。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ProbeSharesAtomicGpuSlotAndRespectsTotalConcurrency(int total)
    {
        var admission = new EncodingAdmission();
        var configuration = new WorkerConfiguration { MaxConcurrentEncodes = total, GpuConcurrency = 2 };
        var capabilities = new WorkerCapabilities { Encoders = [new() { Encoder = "h264_nvenc", Available = true }] };
        using (var probe = admission.TryAcquireProbe(configuration))
        {
            Assert.NotNull(probe);
            Assert.Null(admission.TryAcquireProbe(configuration));
            Assert.Null(admission.TryAcquire(configuration, capabilities, new(10, 8, null), TimeSpan.Zero));
        }
        using var encoding = admission.TryAcquire(configuration, capabilities, new(10, 8, null), TimeSpan.Zero);
        Assert.NotNull(encoding);
        Assert.Null(admission.TryAcquireProbe(configuration));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
