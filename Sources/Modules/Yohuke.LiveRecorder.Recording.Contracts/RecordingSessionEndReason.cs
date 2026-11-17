namespace Yohuke.LiveRecorder.Recording.Contracts;

/// <summary>逻辑直播场次不可逆的结束原因，不包含采集暂停或所有权交接。</summary>
public enum RecordingSessionEndReason
{
    None,
    OfflineGraceExpired,
    MediaTimeout
}
