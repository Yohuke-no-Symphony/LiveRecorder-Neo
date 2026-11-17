using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Services;

internal sealed class WindowsServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>创建由 Windows 服务 API 填充并自动释放的句柄。</summary>
    public WindowsServiceHandle() : base(true)
    {
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);

    /// <inheritdoc />
    protected override bool ReleaseHandle() => CloseServiceHandle(handle);
}
