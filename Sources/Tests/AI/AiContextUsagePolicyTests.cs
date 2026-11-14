using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI.Tests;

/// <summary>验证上下文窗口使用单次容量与实际用量，不因旧阈值或累计成本提前压缩。</summary>
public sealed class AiContextUsagePolicyTests
{
    /// <summary>默认自动模式即使超过旧32K或累计百万参考量也不主动压缩。</summary>
    [Theory]
    [InlineData(32000)]
    [InlineData(1000000)]
    [InlineData(long.MaxValue)]
    public void AutomaticModeDoesNotGuessTheProviderWindow(long inputTokens)
    {
        var settings = new AiSettings { MaximumTokens = 10000 };
        Assert.Equal(0, settings.ContextWindowTokens);
        Assert.False(AiContextUsagePolicy.ShouldCompact(settings, inputTokens, true));
    }

    /// <summary>声明的真实窗口只预留当前输出额度，未接近容量时继续分析。</summary>
    [Fact]
    public void ExplicitWindowReservesOutputWithoutAnArbitraryEarlyThreshold()
    {
        var settings = new AiSettings { ContextWindowTokens = 1000000, MaxOutputTokens = 8192 };
        AiAnalysisPolicy.Validate(settings);
        Assert.False(AiContextUsagePolicy.ShouldCompact(settings, 32000, true));
        Assert.False(AiContextUsagePolicy.ShouldCompact(settings, 991807, true));
        Assert.True(AiContextUsagePolicy.ShouldCompact(settings, 991808, true));
        Assert.False(AiContextUsagePolicy.ShouldCompact(settings, 991808, false));
    }

    /// <summary>中文及协议的保守估算按服务商实际输入校准，避免高估提前压缩。</summary>
    [Fact]
    public void ProviderPromptUsageCalibratesTheCurrentContextEstimate()
    {
        var current = AiContextUsagePolicy.EstimateInputTokens(60000, 50000, 12500);
        Assert.Equal(15000, current);
        var settings = new AiSettings { ContextWindowTokens = 32000, MaxOutputTokens = 4096 };
        Assert.False(AiContextUsagePolicy.ShouldCompact(settings, current, true));
        Assert.Equal(17500, AiContextUsagePolicy.EstimateInputTokens(70000, 50000, 12500));
    }

    /// <summary>校准和回退不溢出，也不把服务商缺失或零用量当作无限可用容量。</summary>
    [Fact]
    public void UnknownUsageAndLargeCountersRemainWellDefined()
    {
        Assert.Equal(60000, AiContextUsagePolicy.EstimateInputTokens(60000, 50000, null));
        Assert.Equal(60000, AiContextUsagePolicy.EstimateInputTokens(60000, 50000, 0));
        Assert.Equal(60000, AiContextUsagePolicy.EstimateInputTokens(60000, 0, 10000));
        Assert.Equal(long.MaxValue, AiContextUsagePolicy.EstimateInputTokens(int.MaxValue, 1, long.MaxValue));
    }

    /// <summary>未知或配置过大的真实窗口收到明确拒绝时仍可收缩交接并重试。</summary>
    [Fact]
    public void ProviderRejectionShrinksOnlyTheRecoveryHandoff()
    {
        var first = AiContextUsagePolicy.RecoveryTarget(120000, 0);
        Assert.Equal(60000, first);
        Assert.Equal(30000, AiContextUsagePolicy.RecoveryTarget(120000, first));
        Assert.Equal(AiAnalysisPolicy.MIN_CONTEXT_TOKEN_TARGET, AiContextUsagePolicy.RecoveryTarget(5000, 8192));
        Assert.Equal(int.MaxValue, AiContextUsagePolicy.RecoveryTarget(long.MaxValue, 0));
    }

    /// <summary>自动模式与百万以上显式窗口可保存，输出必须能放入已知窗口。</summary>
    [Theory]
    [InlineData(0, 1000000)]
    [InlineData(8192, 2048)]
    [InlineData(2000000, 1000000)]
    [InlineData(int.MaxValue, 2048)]
    public void ValidWindowsAreAccepted(int window, int output)
    {
        AiAnalysisPolicy.Validate(new() { ContextWindowTokens = window, MaxOutputTokens = output });
    }

    /// <summary>显式窗口不能为负、过小或被输出预留完全占满。</summary>
    [Theory]
    [InlineData(-1, 2048)]
    [InlineData(8191, 2048)]
    [InlineData(8192, 8192)]
    [InlineData(8192, 1000000)]
    public void ImpossibleExplicitWindowsAreRejected(int window, int output)
    {
        Assert.Throws<InfrastructureException>(() => AiAnalysisPolicy.Validate(new()
        {
            ContextWindowTokens = window, MaxOutputTokens = output
        }));
    }
}
