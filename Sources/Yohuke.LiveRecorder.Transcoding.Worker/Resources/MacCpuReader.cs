using System.Runtime.InteropServices;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Resources;

internal static class MacCpuReader
{
    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern uint mach_host_self();

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern int host_statistics(uint host, int flavor, [Out] uint[] info, ref uint count);

    internal static CpuCounters Read()
    {
        var info = new uint[4];
        var count = 4u;
        if (host_statistics(mach_host_self(), 3, info, ref count) != 0 || count != 4)
        {
            throw new InvalidOperationException("无法读取 macOS CPU 计数器");
        }
        var busy = (double)info[0] + info[1] + info[3];
        return new(busy, busy + info[2], Environment.ProcessorCount);
    }
}
