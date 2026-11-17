using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>当前版本唯一的可授予权限目录。</summary>
public static class PermissionCatalog
{
    public static IReadOnlyList<PermissionDefinition> Definitions { get; } = Array.AsReadOnly<PermissionDefinition>([
        new(PermissionIds.VIEW_RECORDINGS, "recordings", "shell.permissions.viewRecordings", null,
            "mdi-play-box-outline", false, true),
        new(PermissionIds.DOWNLOAD_RECORDINGS, "recordings", "shell.permissions.downloadRecordings",
            "shell.permissions.downloadDescription", "mdi-download-outline", false, true),
        new(PermissionIds.CREATE_CLIPS, "recordings", "shell.permissions.createClips", null, "mdi-content-cut", false,
            true),
        new(PermissionIds.MANAGE_RECORDINGS, "recordings", "shell.permissions.manageRecordings",
            "shell.permissions.manageDescription", "mdi-shield-check-outline", false, false),
        new(PermissionIds.CONFIGURE_RECORDINGS, "recordings", "shell.permissions.configureRecordings",
            "shell.permissions.configureDescription", "mdi-cog-outline", false, false),
        new(PermissionIds.PURGE_RECORDINGS, "recordings", "shell.permissions.purgeRecordings",
            "shell.permissions.purgeDescription", "mdi-delete-forever-outline", true, false),
        new(PermissionIds.UPLOAD_LIVE_MARKERS, "liveMarkers", "shell.permissions.uploadLiveMarkers", null,
            "mdi-upload-outline", false, true),
        new(PermissionIds.MANAGE_LIVE_MARKERS, "liveMarkers", "shell.permissions.manageLiveMarkers",
            "shell.permissions.manageLiveMarkersDescription", "mdi-file-document-edit-outline", true, false),
        new(PermissionIds.GENERATE_LIVE_MARKERS, "liveMarkers", "shell.permissions.generateLiveMarkers",
            "shell.permissions.generateLiveMarkersDescription", "mdi-creation-outline", false, false),
        new(PermissionIds.VIEW_ANALYTICS, "analytics", "shell.permissions.viewAnalytics", null,
            "mdi-chart-timeline-variant", false, false),
        new(PermissionIds.GENERATE_ANALYTICS, "analytics", "shell.permissions.generateAnalytics",
            "shell.permissions.generateDescription", "mdi-creation-outline", false, false)
    ]);

    public static PermissionSet All { get; } = new(Definitions.Select(item => item.Id));

    public static PermissionSet Defaults { get; } =
        new(Definitions.Where(item => item.DefaultGranted).Select(item => item.Id));

    /// <summary>判断整个集合是否只包含当前目录中的权限。</summary>
    public static bool IsValid(PermissionSet permissions) => permissions.All(All.Contains);
}
