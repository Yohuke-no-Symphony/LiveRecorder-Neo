using System.Runtime.InteropServices;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Hosting;

[StructLayout(LayoutKind.Sequential)]
internal struct WindowsWorkerJobIoCounters
{
    internal ulong ReadOperationCount;
    internal ulong WriteOperationCount;
    internal ulong OtherOperationCount;
    internal ulong ReadTransferCount;
    internal ulong WriteTransferCount;
    internal ulong OtherTransferCount;
}
