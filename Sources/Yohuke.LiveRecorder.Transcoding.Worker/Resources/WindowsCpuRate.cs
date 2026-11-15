using System.Runtime.InteropServices;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Resources;

[StructLayout(LayoutKind.Sequential)]
internal struct WindowsCpuRate
{
    internal uint ControlFlags;
    internal uint Rate;
}
