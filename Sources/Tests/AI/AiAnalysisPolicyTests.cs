using System.Text.Json;
using Xunit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI.Tests;

/// <summary>验证可配置的模型输出及调用次数边界与旧配置兼容性。</summary>
public sealed class AiAnalysisPolicyTests
{
    /// <summary>未显式保存调用限制时不限，新上下文参数有独立默认值。</summary>
    [Fact]
    public void LegacySettingsReceiveCompatibleExecutionLimits()
    {
        var settings = JsonSerializer.Deserialize<AiSettings>("{}")!;
        Assert.Equal(2048, settings.MaxOutputTokens);
        Assert.Equal(0, settings.MaxModelRequests);
        Assert.Equal(0, settings.MaxToolCalls);
        Assert.Equal(32000, settings.ContextTokenTarget);
        Assert.Equal(0, settings.ContextWindowTokens);
        Assert.Equal(180, settings.RequestTimeoutSeconds);
        Assert.Equal(2, settings.MaxRequestRetries);
        Assert.Equal(1000000, settings.MaximumTokens);
        Assert.Equal("Default", settings.ThinkingMode);
        Assert.Equal("Default", settings.ReasoningEffort);
        AiAnalysisPolicy.Validate(settings);
    }

    /// <summary>输出值不预设服务商上限，调用次数支持零表示不限。</summary>
    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(1000000, 1000000, 1000000)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue)]
    public void ExecutionLimitBoundariesAndIndependentValuesAreAccepted(int outputTokens, int modelRequests, int toolCalls)
    {
        AiAnalysisPolicy.Validate(new()
        {
            MaxOutputTokens = outputTokens,
            MaxModelRequests = modelRequests,
            MaxToolCalls = toolCalls
        });
    }

    /// <summary>每项执行额度越界均拒绝，禁用服务也不会保存无效额度。</summary>
    [Theory]
    [InlineData(0, 24, 48)]
    [InlineData(-1, 24, 48)]
    [InlineData(2048, -1, 48)]
    [InlineData(2048, 24, -1)]
    public void InvalidExecutionLimitsAreRejected(int outputTokens, int modelRequests, int toolCalls)
    {
        var error = Assert.Throws<InfrastructureException>(() => AiAnalysisPolicy.Validate(new()
        {
            MaxOutputTokens = outputTokens,
            MaxModelRequests = modelRequests,
            MaxToolCalls = toolCalls
        }));
        Assert.Equal("AI_SETTINGS_INVALID", error.Code);
    }

    /// <summary>参考 token 数支持一百万及以上，按保存区间计算参考值。</summary>
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(1000000, 1000000, 2000000)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue)]
    public void ReferenceTokenValuesAcceptPositiveIntegersWithoutFixedUpperBounds(int perHour, int minimum, int maximum)
    {
        var settings = new AiSettings { TokensPerHour = perHour, MinimumTokens = minimum, MaximumTokens = maximum };
        AiAnalysisPolicy.Validate(settings);
        Assert.Equal(Math.Clamp(perHour, minimum, maximum), AiAnalysisPolicy.Budget(settings, 3600000));
    }

    /// <summary>参考值必须为正整数且区间顺序有效。</summary>
    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(-1, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 2, 1)]
    [InlineData(1, 1, 0)]
    public void InvalidReferenceTokenRangesAreRejected(int perHour, int minimum, int maximum)
    {
        var error = Assert.Throws<InfrastructureException>(() => AiAnalysisPolicy.Validate(new()
        {
            TokensPerHour = perHour, MinimumTokens = minimum, MaximumTokens = maximum
        }));
        Assert.Equal("AI_SETTINGS_INVALID", error.Code);
    }

    /// <summary>新的默认参考值不覆盖已有用户显式保存的设置。</summary>
    [Fact]
    public void ExplicitPreviouslySavedReferenceValuesArePreserved()
    {
        var settings = JsonSerializer.Deserialize<AiSettings>("""{"MaximumTokens":100000,"MaxModelRequests":24,"MaxToolCalls":48}""")!;
        Assert.Equal(100000, settings.MaximumTokens);
        Assert.Equal(24, settings.MaxModelRequests);
        Assert.Equal(48, settings.MaxToolCalls);
        AiAnalysisPolicy.Validate(settings);
    }

    /// <summary>无响应超时及有限重试可关闭，正值在保存后保持不变。</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(180, 2)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void RequestRecoverySettingsRoundTrip(int timeoutSeconds, int retries)
    {
        var original = new AiSettings { RequestTimeoutSeconds = timeoutSeconds, MaxRequestRetries = retries };
        AiAnalysisPolicy.Validate(original);
        var restored = JsonSerializer.Deserialize<AiSettings>(JsonSerializer.Serialize(original))!;
        Assert.Equal(timeoutSeconds, restored.RequestTimeoutSeconds);
        Assert.Equal(retries, restored.MaxRequestRetries);
    }

    /// <summary>负数超时与重试次数不允许写入后台配置。</summary>
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void NegativeRequestRecoverySettingsAreRejected(int timeoutSeconds, int retries)
    {
        var error = Assert.Throws<InfrastructureException>(() => AiAnalysisPolicy.Validate(new()
        {
            RequestTimeoutSeconds = timeoutSeconds, MaxRequestRetries = retries
        }));
        Assert.Equal("AI_SETTINGS_INVALID", error.Code);
    }

    /// <summary>旧压缩阈值保留归档兼容，不被迁移成真实模型窗口。</summary>
    [Theory]
    [InlineData(8192)]
    [InlineData(1000000)]
    [InlineData(int.MaxValue)]
    public void LegacyContextTargetsDoNotBecomeModelCapacity(int target)
    {
        var settings = new AiSettings { ContextTokenTarget = target, MaxOutputTokens = 1000000 };
        AiAnalysisPolicy.Validate(settings);
        var restored = JsonSerializer.Deserialize<AiSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(target, restored.ContextTokenTarget);
        Assert.Equal(0, restored.ContextWindowTokens);
        Assert.Equal(1000000, restored.MaxOutputTokens);
    }

    /// <summary>已不参与执行的旧阈值不会阻止有效的新窗口配置保存。</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(8191)]
    public void IgnoredLegacyThresholdsDoNotInvalidateCurrentSettings(int target)
    {
        var settings = new AiSettings { ContextTokenTarget = target, ContextWindowTokens = 1000000 };
        AiAnalysisPolicy.Validate(settings);
        Assert.False(AiContextUsagePolicy.ShouldCompact(settings, 32000, true));
    }

    /// <summary>思考开关和强度只接受已定义的配置值。</summary>
    [Theory]
    [InlineData("default", "Default")]
    [InlineData("Unknown", "High")]
    [InlineData("Enabled", "Medium")]
    [InlineData(null, "Default")]
    [InlineData("Default", null)]
    public void InvalidThinkingOptionsAreRejected(string? mode, string? effort)
    {
        var error = Assert.Throws<InfrastructureException>(() => AiAnalysisPolicy.Validate(new()
        {
            ThinkingMode = mode!, ReasoningEffort = effort!
        }));
        Assert.Equal("AI_SETTINGS_INVALID", error.Code);
    }

    /// <summary>受支持思考配置可序列化保存，禁用时保留强度选择以便以后恢复。</summary>
    [Theory]
    [InlineData("Default", "Default")]
    [InlineData("Enabled", "Low")]
    [InlineData("Enabled", "High")]
    [InlineData("Enabled", "Max")]
    [InlineData("Disabled", "High")]
    public void ValidThinkingOptionsRoundTripWithoutChangingExecutionLimits(string mode, string effort)
    {
        var original = new AiSettings
        {
            Provider = "DeepSeek", ThinkingMode = mode, ReasoningEffort = effort,
            MaxOutputTokens = 8192, MaxModelRequests = 16, MaxToolCalls = 96
        };
        var settings = JsonSerializer.Deserialize<AiSettings>(JsonSerializer.Serialize(original))!;
        AiAnalysisPolicy.Validate(settings);
        Assert.Equal(mode, settings.ThinkingMode);
        Assert.Equal(effort, settings.ReasoningEffort);
        Assert.Equal(8192, settings.MaxOutputTokens);
        Assert.Equal(16, settings.MaxModelRequests);
        Assert.Equal(96, settings.MaxToolCalls);
    }
}
