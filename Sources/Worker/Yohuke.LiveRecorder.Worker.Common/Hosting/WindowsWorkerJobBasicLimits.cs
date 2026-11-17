using System.Runtime.InteropServices;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Hosting;

[StructLayout(LayoutKind.Sequential)]
internal struct WindowsWorkerJobBasicLimits
{
    internal long PerProcessUserTimeLimit;
    internal long PerJobUserTimeLimit;
    internal uint LimitFlags;
    internal nuint MinimumWorkingSetSize;
    internal nuint MaximumWorkingSetSize;
    internal uint ActiveProcessLimit;
    internal nuint Affinity;
    internal uint PriorityClass;
    internal uint SchedulingClass;
}
