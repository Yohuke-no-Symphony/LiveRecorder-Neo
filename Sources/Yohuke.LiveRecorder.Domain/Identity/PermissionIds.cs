namespace Yohuke.LiveRecorder.Domain;

/// <summary>持久化权限的稳定标识。</summary>
public static class PermissionIds
{
    public const string VIEW_RECORDINGS = "recordings.view";
    public const string DOWNLOAD_RECORDINGS = "recordings.download";
    public const string CREATE_CLIPS = "clips.create";
    public const string UPLOAD_LIVE_MARKERS = "live-markers.upload";
    public const string MANAGE_LIVE_MARKERS = "live-markers.manage";
    public const string GENERATE_LIVE_MARKERS = "live-markers.generate";
    public const string MANAGE_RECORDINGS = "recordings.manage";
    public const string CONFIGURE_RECORDINGS = "recordings.configure";
    public const string VIEW_ANALYTICS = "analytics.view";
    public const string GENERATE_ANALYTICS = "analytics.generate";
    public const string PURGE_RECORDINGS = "recordings.purge";
}
