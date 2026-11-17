using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Application.AI;

/// <summary>以模型上下文容量和实际单次输入用量判断容量压缩，与累计用量及停滞恢复分开。</summary>
public static class AiContextUsagePolicy
{
    /// <summary>用最近一次服务商输入用量校准本地估算，未知时保留原估算。</summary>
    public static long EstimateInputTokens(int rawEstimate, int previousRawEstimate, long? previousPromptTokens)
    {
        if (rawEstimate < 0 || previousRawEstimate <= 0 || previousPromptTokens is not > 0)
        {
            return Math.Max(0, rawEstimate);
        }
        var calibrated = decimal.Ceiling((decimal)rawEstimate * previousPromptTokens.Value / previousRawEstimate);
        return (long)Math.Clamp(calibrated, 0, long.MaxValue);
    }

    /// <summary>显式窗口接近本次输入与输出预留之和时压缩；自动模式等待服务商明确的容量拒绝。</summary>
    public static bool ShouldCompact(AiSettings settings, long inputTokens, bool hasHistory)
    {
        if (!hasHistory || settings.ContextWindowTokens == 0)
        {
            return false;
        }
        var inputCapacity = (long)settings.ContextWindowTokens - settings.MaxOutputTokens;
        return inputTokens >= inputCapacity;
    }

    /// <summary>明确的上下文拒绝后逐步缩小当前交接，仅用于该次容量恢复。</summary>
    public static int RecoveryTarget(long rejectedInputTokens, int previousTarget)
    {
        var target = Math.Max(AiAnalysisPolicy.MIN_CONTEXT_TOKEN_TARGET, rejectedInputTokens / 2);
        if (previousTarget > AiAnalysisPolicy.MIN_CONTEXT_TOKEN_TARGET)
        {
            target = Math.Min(target, Math.Max(AiAnalysisPolicy.MIN_CONTEXT_TOKEN_TARGET, previousTarget / 2));
        }
        return (int)Math.Min(target, int.MaxValue);
    }
}
