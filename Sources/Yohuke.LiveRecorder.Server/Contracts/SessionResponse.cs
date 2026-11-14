using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>包含当前自动回收期限的场次响应，不将动态期限写入录像档案。</summary>
public sealed class SessionResponse(BroadcastSession session, DateTime? expiresAtUtc)
{
    public string Id => session.Id;
    public DateTime CreatedAtUtc => session.CreatedAtUtc;
    public string StreamerId => session.StreamerId;
    public long RoomId => session.RoomId;
    public string Title => session.Title;
    public string CoverUrl => session.CoverUrl;
    public string? CoverRelativePath => session.CoverRelativePath;
    public string DirectoryPath => session.DirectoryPath;
    public long LiveTime => session.LiveTime;
    public DateTime StartedAtUtc => session.StartedAtUtc;
    public DateTime? EndedAtUtc => session.EndedAtUtc;
    public DateTime LastMediaAtUtc => session.LastMediaAtUtc;
    public string State => session.State;
    public bool HasDanmu => session.HasDanmu;
    public string? TranslationDanmuPatternOverride => session.TranslationDanmuPatternOverride;
    public int ConfigurationRevision => session.ConfigurationRevision;
    public long TotalBytes => session.TotalBytes;
    public string? Error => session.Error;
    public string? ErrorCode => session.ErrorCode;
    public List<string> ErrorArguments => session.ErrorArguments;
    public bool IsTrashed => session.IsTrashed;
    public bool IsPermanent => session.IsPermanent;
    public DateTime? RetentionStartedAtUtc => session.RetentionStartedAtUtc;
    public DateTime? ExpiresAtUtc { get; } = expiresAtUtc;
    public int MediaRevision => session.MediaRevision;
    public string? MediaRevisionJobId => session.MediaRevisionJobId;
    public int MediaRevisionAttempt => session.MediaRevisionAttempt;
    public bool IsHidden => session.IsHidden;
    public List<string> GroupedSessionIds => session.GroupedSessionIds;
    public List<EventArchiveSource> EventSources => session.EventSources;
    public bool ProjectionReady => session.ProjectionReady;
}
