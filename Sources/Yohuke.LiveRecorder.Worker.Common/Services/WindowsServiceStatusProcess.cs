using System.Runtime.InteropServices;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Services;

[StructLayout(LayoutKind.Sequential)]
internal struct WindowsServiceStatusProcess
{
    internal uint ServiceType;
    internal uint CurrentState;
    internal uint ControlsAccepted;
    internal uint Win32ExitCode;
    internal uint ServiceSpecificExitCode;
    internal uint CheckPoint;
    internal uint WaitHint;
    internal uint ProcessId;
    internal uint ServiceFlags;
}
