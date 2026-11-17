namespace Yohuke.LiveRecorder.Transcoding.Worker.Services;

internal sealed record WorkerServiceIdentity
{
    internal required string ServiceName { get; init; }
    internal required string LaunchdLabel { get; init; }
    internal required string DirectoryName { get; init; }
    internal required string DisplayName { get; init; }
    internal required string OwnerMarker { get; init; }

    internal string LinuxUnit => ServiceName + ".service";
    internal string LinuxUnitPath => "/etc/systemd/system/" + LinuxUnit;
    internal string LinuxBundleDirectory => "/var/cache/" + ServiceName + "/bundles";
    internal string MacDefinitionDirectory => "/Library/Application Support/Yohuke.LiveRecorder/" + DirectoryName;
    internal string MacDefinitionPath => Path.Combine(MacDefinitionDirectory, LaunchdLabel + ".plist");
    internal string MacAutostartPath => "/Library/LaunchDaemons/" + LaunchdLabel + ".plist";
    internal string MacServiceTarget => "system/" + LaunchdLabel;
    internal string WindowsRegistryPath => @"SYSTEM\CurrentControlSet\Services\" + ServiceName;

    internal static WorkerServiceIdentity Transcoding { get; } = new()
    {
        ServiceName = "yohuke-live-recorder-worker",
        LaunchdLabel = "com.yohuke.live-recorder.worker",
        DirectoryName = "WorkerService",
        DisplayName = "Yohuke Live Recorder Transcoding Worker",
        OwnerMarker = "Managed by Yohuke.LiveRecorder.Transcoding.Worker"
    };

    internal static WorkerServiceIdentity Clipping { get; } = new()
    {
        ServiceName = "yohuke-live-recorder-clipping-worker",
        LaunchdLabel = "com.yohuke.live-recorder.clipping-worker",
        DirectoryName = "ClippingWorkerService",
        DisplayName = "Yohuke Live Recorder Clipping Worker",
        OwnerMarker = "Managed by Yohuke.LiveRecorder.Clipping.Worker"
    };

    internal static WorkerServiceIdentity Recording { get; } = new()
    {
        ServiceName = "yohuke-live-recorder-recording-worker",
        LaunchdLabel = "com.yohuke.live-recorder.recording-worker",
        DirectoryName = "RecordingWorkerService",
        DisplayName = "Yohuke Live Recorder Recording Worker",
        OwnerMarker = "Managed by Yohuke.LiveRecorder.Recording.Worker"
    };
}
