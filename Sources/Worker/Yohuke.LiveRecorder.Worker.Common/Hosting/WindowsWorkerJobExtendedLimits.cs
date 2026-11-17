using System.Runtime.InteropServices;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Hosting;

[StructLayout(LayoutKind.Sequential)]
internal struct WindowsWorkerJobExtendedLimits
{
    internal WindowsWorkerJobBasicLimits BasicLimitInformation;
    internal WindowsWorkerJobIoCounters IoInfo;
    internal nuint ProcessMemoryLimit;
    internal nuint JobMemoryLimit;
    internal nuint PeakProcessMemoryUsed;
    internal nuint PeakJobMemoryUsed;
}
