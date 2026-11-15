using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Resources;

[SupportedOSPlatform("windows")]
internal static class WindowsCpuReader
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort groupNumber);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool inJob);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int informationClass, out WindowsCpuRate rate,
        uint length, IntPtr returnLength);

    internal static CpuCounters Read()
    {
        using var process = Process.GetCurrentProcess();
        var processors = GetActiveProcessorCount(ushort.MaxValue);
        if (processors > 64 || System.Numerics.BitOperations.PopCount((ulong)process.ProcessorAffinity) != processors
            || !GetSystemTimes(out var idle, out var kernel, out var user))
        {
            throw new InvalidOperationException("无法可靠读取受限 CPU 资源域的负载");
        }
        if (!IsProcessInJob(process.Handle, IntPtr.Zero, out var inJob))
        {
            throw new InvalidOperationException("无法读取 Windows CPU 资源限制");
        }
        if (inJob && (!QueryInformationJobObject(IntPtr.Zero, 15, out var rate, (uint)Marshal.SizeOf<WindowsCpuRate>(), IntPtr.Zero)
                      || (rate.ControlFlags & 1) != 0))
        {
            throw new InvalidOperationException("Windows Job CPU 配额负载不可可靠计量，自动 CPU 补位已禁用");
        }
        return new(kernel + (double)user - idle, kernel + (double)user, processors);
    }
}
