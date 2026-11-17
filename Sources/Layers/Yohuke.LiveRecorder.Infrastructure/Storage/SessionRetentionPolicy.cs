using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Storage;

/// <summary>按当前录像保留设置计算自动移入回收站的期限。</summary>
public static class SessionRetentionPolicy
{
    /// <summary>结束、恢复或取消永久保留后开始计时，受保护、尚未结束或不参与自动回收的录像没有期限。</summary>
    public static DateTime? GetExpiresAtUtc(BroadcastSession session, ServerSettings settings)
    {
        if (settings.RecordingRetentionDays <= 0 || session.IsPermanent || session.IsTrashed || session.IsHidden ||
            !session.ProjectionReady || session.State == "Recording" || session.EndedAtUtc is not { } endedAt)
        {
            return null;
        }

        var startedAt = session.RetentionStartedAtUtc is { } retentionStartedAt && retentionStartedAt > endedAt
            ? retentionStartedAt
            : endedAt;
        return startedAt.AddDays(settings.RecordingRetentionDays);
    }

    /// <summary>判断录像是否已经到达当前自动回收期限，包含期限本身。</summary>
    public static bool IsExpired(BroadcastSession session, ServerSettings settings, DateTime now)
    {
        return GetExpiresAtUtc(session, settings) is { } expiresAt && expiresAt <= now;
    }
}
