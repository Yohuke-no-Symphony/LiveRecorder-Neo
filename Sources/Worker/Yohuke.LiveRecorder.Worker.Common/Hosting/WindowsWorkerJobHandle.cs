using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Hosting;

[SupportedOSPlatform("windows")]
internal sealed class WindowsWorkerJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal WindowsWorkerJobHandle() : base(true)
    {
    }

    /// <inheritdoc />
    protected override bool ReleaseHandle() => WindowsWorkerJobNative.CloseHandle(handle);
}
