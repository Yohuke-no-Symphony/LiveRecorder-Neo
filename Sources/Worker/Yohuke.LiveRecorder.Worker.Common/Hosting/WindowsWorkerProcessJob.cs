using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Hosting;

[SupportedOSPlatform("windows")]
internal static class WindowsWorkerProcessJob
{
    private const uint KILL_ON_JOB_CLOSE = 0x00002000;
    private const int EXTENDED_LIMIT_INFORMATION = 9;
    private static readonly Lock sync = new();
    private static WindowsWorkerJobHandle? processJob;

    internal static void AttachCurrentProcess()
    {
        lock (sync)
        {
            if (processJob is not null)
            {
                return;
            }
            var job = WindowsWorkerJobNative.CreateJobObject(0, null);
            try
            {
                if (job.IsInvalid)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the worker process job.");
                }
                var limits = new WindowsWorkerJobExtendedLimits
                {
                    BasicLimitInformation = new() { LimitFlags = KILL_ON_JOB_CLOSE }
                };
                if (!WindowsWorkerJobNative.SetInformationJobObject(job, EXTENDED_LIMIT_INFORMATION, ref limits,
                        (uint)Marshal.SizeOf<WindowsWorkerJobExtendedLimits>()))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not configure the worker process job.");
                }
                using var current = Process.GetCurrentProcess();
                if (!WindowsWorkerJobNative.AssignProcessToJobObject(job, current.SafeHandle))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not bind worker child processes to its lifetime.");
                }
                processJob = job;
            }
            catch
            {
                job.Dispose();
                throw;
            }
        }
    }
}
