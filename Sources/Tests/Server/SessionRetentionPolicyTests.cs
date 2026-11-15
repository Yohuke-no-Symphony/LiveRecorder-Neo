using Xunit;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Storage;

namespace Yohuke.LiveRecorder.Tests;

/// <summary>验证录像展示期限与自动回收使用一致的保留规则。</summary>
public sealed class SessionRetentionPolicyTests
{
    /// <summary>结束与恢复时间取较晚者，旧档案没有恢复时间时使用结束时间。</summary>
    [Theory]
    [InlineData(null, 7)]
    [InlineData(-2, 7)]
    [InlineData(0, 7)]
    [InlineData(2, 9)]
    public void ExpiryUsesTheLaterOfCompletionAndRestore(int? restoredDaysAfterEnd, int expectedDaysAfterEnd)
    {
        var session = CreateSession();
        var endedAt = session.EndedAtUtc!.Value;
        session.RetentionStartedAtUtc = restoredDaysAfterEnd is { } offset ? endedAt.AddDays(offset) : null;
        var settings = new ServerSettings { RecordingRetentionDays = 7 };

        var expiresAt = SessionRetentionPolicy.GetExpiresAtUtc(session, settings);

        Assert.Equal(endedAt.AddDays(expectedDaysAfterEnd), expiresAt);
        Assert.Equal(DateTimeKind.Utc, expiresAt!.Value.Kind);
    }

    /// <summary>既有录像的期限立即反映当前保留设置，不缓存旧期限。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(3650)]
    public void ExpiryReflectsTheCurrentRetentionPeriod(int days)
    {
        var session = CreateSession();
        var settings = new ServerSettings { RecordingRetentionDays = 7 };
        Assert.Equal(session.EndedAtUtc!.Value.AddDays(7), SessionRetentionPolicy.GetExpiresAtUtc(session, settings));

        settings.RecordingRetentionDays = days;

        Assert.Equal(session.EndedAtUtc.Value.AddDays(days), SessionRetentionPolicy.GetExpiresAtUtc(session, settings));
    }

    /// <summary>未进入自动回收范围的录像没有确定的到期时间，也不会判定为到期。</summary>
    [Theory]
    [InlineData("permanent")]
    [InlineData("disabled")]
    [InlineData("negative")]
    [InlineData("unfinished")]
    [InlineData("recording")]
    [InlineData("trashed")]
    [InlineData("hidden")]
    [InlineData("unready")]
    public void IneligibleRecordingsHaveNoExpiry(string condition)
    {
        var session = CreateSession();
        var settings = new ServerSettings { RecordingRetentionDays = 7 };
        switch (condition)
        {
            case "permanent":
                session.IsPermanent = true;
                break;
            case "disabled":
                settings.RecordingRetentionDays = 0;
                break;
            case "negative":
                settings.RecordingRetentionDays = -1;
                break;
            case "unfinished":
                session.EndedAtUtc = null;
                break;
            case "recording":
                session.State = "Recording";
                break;
            case "trashed":
                session.IsTrashed = true;
                break;
            case "hidden":
                session.IsHidden = true;
                break;
            case "unready":
                session.ProjectionReady = false;
                break;
        }

        Assert.Null(SessionRetentionPolicy.GetExpiresAtUtc(session, settings));
        Assert.False(SessionRetentionPolicy.IsExpired(session, settings, new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    /// <summary>到期时刻本身已允许回收，尚差一毫秒的录像仍保留。</summary>
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void ExpirationIncludesTheDeadline(int millisecondsAfterDeadline, bool expectedExpired)
    {
        var session = CreateSession();
        var settings = new ServerSettings { RecordingRetentionDays = 7 };
        var deadline = session.EndedAtUtc!.Value.AddDays(7);

        Assert.Equal(expectedExpired,
            SessionRetentionPolicy.IsExpired(session, settings, deadline.AddMilliseconds(millisecondsAfterDeadline)));
    }

    /// <summary>未提供新的保留起点时，策略仍按原始期限计算。</summary>
    [Fact]
    public void RemovingProtectionWithoutUpdatingRetentionStartKeepsTheOriginalDeadline()
    {
        var session = CreateSession();
        var settings = new ServerSettings { RecordingRetentionDays = 7 };
        var deadline = session.EndedAtUtc!.Value.AddDays(7);
        session.IsPermanent = true;
        Assert.Null(SessionRetentionPolicy.GetExpiresAtUtc(session, settings));

        session.IsPermanent = false;

        Assert.Equal(deadline, SessionRetentionPolicy.GetExpiresAtUtc(session, settings));
        Assert.True(SessionRetentionPolicy.IsExpired(session, settings, deadline));
    }

    private static BroadcastSession CreateSession() => new()
    {
        EndedAtUtc = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc),
        State = "Completed"
    };
}
