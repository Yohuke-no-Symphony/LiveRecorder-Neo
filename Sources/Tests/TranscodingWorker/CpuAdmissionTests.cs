using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Resources;
using Yohuke.LiveRecorder.Transcoding.Worker.Scheduling;
using Xunit;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Tests;

/// <summary>验证 CPU 采样可信度和 GPU 优先的原子并发准入。</summary>
public sealed class CpuAdmissionTests
{
    /// <summary>采样窗口必须完整、连续且足够新。</summary>
    [Fact]
    public void CpuWindowRequiresCompleteFreshContinuousSamples()
    {
        var window = new CpuLoadWindow();
        for (var second = 0; second < 30; second++)
        {
            window.Add(TimeSpan.FromSeconds(second), new(100 + second * 20, 1000 + second * 100, 8));
        }
        Assert.Null(window.Read(TimeSpan.FromSeconds(29), 30).AveragePercent);
        window.Add(TimeSpan.FromSeconds(30), new(700, 4000, 8));
        Assert.Equal(20, window.Read(TimeSpan.FromSeconds(30), 30).AveragePercent);
        Assert.Null(window.Read(TimeSpan.FromSeconds(34), 30).AveragePercent);
        window.Add(TimeSpan.FromSeconds(35), new(800, 4500, 8));
        Assert.Null(window.Read(TimeSpan.FromSeconds(35), 30).AveragePercent);
    }

    /// <summary>容器配额使用率参与判断，资源域改变时丢弃旧窗口。</summary>
    [Fact]
    public void CpuWindowUsesQuotaUtilizationAndResetsOnResourceChanges()
    {
        var window = new CpuLoadWindow();
        for (var second = 0; second <= 30; second++)
        {
            window.Add(TimeSpan.FromSeconds(second), new(100 + second * 5, 1000 + second * 100, 2, second * 1.6, "container"));
        }
        Assert.Equal(80, window.Read(TimeSpan.FromSeconds(30), 30).AveragePercent);
        window.Add(TimeSpan.FromSeconds(31), new(255, 4100, 1.5, 50, "container"));
        Assert.Null(window.Read(TimeSpan.FromSeconds(31), 30).AveragePercent);
        window.Add(TimeSpan.FromSeconds(32), null, "指标不可用");
        Assert.Equal("指标不可用", window.Read(TimeSpan.FromSeconds(32), 30).UnavailableReason);
    }

    /// <summary>等待时间、负载阈值和有效核预算必须同时满足。</summary>
    [Theory]
    [InlineData(60, 20, 8, false)]
    [InlineData(61, 60, 8, false)]
    [InlineData(61, 59.9, 8, true)]
    [InlineData(61, 20, 1.5, false)]
    public void CpuFallbackHonorsStrictThresholds(double seconds, double percent, double processors, bool allowed)
    {
        var admission = new EncodingAdmission();
        using var reservation = admission.TryAcquire(new(), Software(), new(percent, processors, null), TimeSpan.FromSeconds(seconds));
        Assert.Equal(allowed, reservation is not null);
        if (reservation is not null)
        {
            Assert.Equal(4, reservation.Threads);
        }
    }

    /// <summary>并发请求不能重复领取同一槽位，重复释放不影响计数。</summary>
    [Fact]
    public async Task ConcurrentRequestsReserveOnlyOneCpuSlotAndReleaseExactlyOnce()
    {
        var admission = new EncodingAdmission();
        var reservations = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
            admission.TryAcquire(new(), Software(), new(10, 8, null), TimeSpan.FromMinutes(2)))));
        var reservation = Assert.Single(reservations, item => item is not null)!;
        Assert.Equal((0, 1), admission.Running);
        reservation.Dispose();
        reservation.Dispose();
        Assert.Equal((0, 0), admission.Running);
    }

    /// <summary>GPU 恢复后保留既有 CPU 任务，同时遵守总并发。</summary>
    [Fact]
    public void GpuRecoveryPreservesCpuJobAndTotalConcurrency()
    {
        var admission = new EncodingAdmission();
        var settings = new WorkerConfiguration { MaxConcurrentEncodes = 1 };
        var capabilities = Software();
        using var cpu = admission.TryAcquire(settings, capabilities, new(10, 8, null), TimeSpan.FromMinutes(2));
        Assert.NotNull(cpu);
        capabilities.Encoders.Add(new() { Encoder = "h264_nvenc", Available = true });
        Assert.Null(admission.TryAcquire(settings, capabilities, new(10, 8, null), TimeSpan.FromMinutes(2)));
        settings.MaxConcurrentEncodes = 2;
        using var gpu = admission.TryAcquire(settings, capabilities, CpuLoadSnapshot.Unknown("不可用"), TimeSpan.Zero);
        Assert.Equal("h264_nvenc", gpu?.Encoder.Encoder);
        Assert.Equal((1, 1), admission.Running);
    }

    /// <summary>未知资源指标禁止补位，CPU 集合输入严格解析。</summary>
    [Fact]
    public void UnknownMetricsNeverStartSoftwareEncoding()
    {
        using var result = new EncodingAdmission().TryAcquire(new(), Software(), CpuLoadSnapshot.Unknown("不可用"), TimeSpan.FromHours(2));
        Assert.Null(result);
        Assert.Equal(new[] { 0, 1, 2, 4, 7, 8 }, LinuxCpuReader.ParseCpuList("0-2,4,7-8").Order());
        Assert.Throws<InvalidDataException>(() => LinuxCpuReader.ParseCpuList("2-1"));
    }

    private static WorkerCapabilities Software() => new() { Encoders = [new() { Encoder = "libx264", Available = true }] };

    /// <summary>多个软件任务共享总线程预算，无剩余能力时允许任务退队。</summary>
    [Fact]
    public void MultipleCpuEncodesShareOneThreadBudgetAndDisabledCapacityCanReturnToQueue()
    {
        var settings = new WorkerConfiguration { MaxConcurrentEncodes = 64, CpuConcurrency = 64 };
        var admission = new EncodingAdmission();
        var reservations = Enumerable.Range(0, 64).Select(_ => admission.TryAcquire(settings, Software(), new(10, 8, null), TimeSpan.FromMinutes(2)))
            .OfType<EncodingReservation>().ToArray();
        Assert.Equal(4, reservations.Sum(item => item.Threads));
        Assert.Equal(4, reservations.Length);
        Assert.False(EncodingAdmission.HasPossibleCapacity(settings with { CpuFallbackMode = "Disabled" }, Software(), new(10, 8, null)));
        Assert.False(EncodingAdmission.HasPossibleCapacity(settings, Software(), new(10, 1, null)));
        foreach (var reservation in reservations)
        {
            reservation.Dispose();
        }
        Assert.Equal((0, 0), admission.Running);
    }
}
