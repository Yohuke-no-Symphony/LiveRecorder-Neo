using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Services;

[SupportedOSPlatform("windows")]
internal static class WindowsWorkerInspection
{
    internal const string LOG_SOURCE = "Application / " + WorkerServices.SERVICE_NAME;
    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern WindowsServiceHandle OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern WindowsServiceHandle OpenService(WindowsServiceHandle manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(WindowsServiceHandle service, int informationLevel,
        out WindowsServiceStatusProcess status, int bufferSize, out int bytesNeeded);

    internal static Task<WorkerServiceStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var manager = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (manager.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            using var serviceHandle = OpenService(manager, WorkerServices.SERVICE_NAME, SERVICE_QUERY_STATUS);
            if (serviceHandle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == 1060)
                {
                    return Task.FromResult(new WorkerServiceStatus { LogSource = LOG_SOURCE });
                }
                if (error == 1072)
                {
                    WindowsWorkerService.RequireOwnedService();
                    return Task.FromResult(new WorkerServiceStatus
                    {
                        Installed = true,
                        State = "Marked for deletion",
                        LogSource = LOG_SOURCE
                    });
                }
                throw new Win32Exception(error);
            }
            WindowsWorkerService.RequireOwnedService();
            if (!QueryServiceStatusEx(serviceHandle, 0, out var status, Marshal.SizeOf<WindowsServiceStatusProcess>(), out _))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            cancellationToken.ThrowIfCancellationRequested();
            using var service = new ServiceController(WorkerServices.SERVICE_NAME);
            return Task.FromResult(DescribeStatus(status, service.StartType));
        }
        catch (Exception exception) when (exception is not WorkerServiceException and not OperationCanceledException)
        {
            throw Failure("Unable to read the Windows worker service status with the current account.", exception);
        }
    }

    internal static WorkerServiceStatus DescribeStatus(WindowsServiceStatusProcess status, ServiceStartMode startup)
    {
        var state = (ServiceControllerStatus)status.CurrentState;
        var validProcess = state is ServiceControllerStatus.Running or ServiceControllerStatus.Paused
            or ServiceControllerStatus.PausePending or ServiceControllerStatus.ContinuePending;
        return new()
        {
            Installed = true,
            IsRunning = state == ServiceControllerStatus.Running,
            State = state switch
            {
                ServiceControllerStatus.Stopped => "Stopped",
                ServiceControllerStatus.StartPending => "Start pending",
                ServiceControllerStatus.StopPending => "Stop pending",
                ServiceControllerStatus.Running => "Running",
                ServiceControllerStatus.ContinuePending => "Continue pending",
                ServiceControllerStatus.PausePending => "Pause pending",
                ServiceControllerStatus.Paused => "Paused",
                _ => "Unknown (" + status.CurrentState.ToString(CultureInfo.InvariantCulture) + ")"
            },
            Startup = startup switch
            {
                ServiceStartMode.Automatic => "Automatic",
                ServiceStartMode.Manual => "Manual",
                ServiceStartMode.Disabled => "Disabled",
                ServiceStartMode.Boot => "Boot",
                ServiceStartMode.System => "System",
                _ => "Unknown (" + ((int)startup).ToString(CultureInfo.InvariantCulture) + ")"
            },
            ProcessId = validProcess && status.ProcessId is > 0 and <= int.MaxValue ? (int)status.ProcessId : null,
            LogSource = LOG_SOURCE
        };
    }

    internal static async Task FollowLogAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var status = await GetStatusAsync(cancellationToken);
            if (!status.Installed)
            {
                throw new WorkerServiceException("The worker service is not installed. Run 'install' first.");
            }
            Console.WriteLine("Following worker service logs. Press Ctrl+C to stop.");
            var snapshot = ReadHistory(cancellationToken);
            foreach (var message in snapshot.Messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Console.WriteLine(message);
            }
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var query = new EventLogQuery("Application", PathType.LogName, WorkerQuery());
            using var watcher = new EventLogWatcher(query, snapshot.Bookmark, snapshot.Bookmark is null);
            void RecordWritten(object? sender, EventRecordWrittenEventArgs args)
            {
                using var record = args.EventRecord;
                try
                {
                    if (args.EventException is { } error)
                    {
                        completion.TrySetException(Failure("The Windows worker event log subscription failed.", error));
                        return;
                    }
                    if (record is not null && !cancellationToken.IsCancellationRequested)
                    {
                        Console.WriteLine(FormatRecord(record));
                    }
                }
                catch (Exception exception)
                {
                    completion.TrySetException(Failure("Unable to read a Windows worker event log entry.", exception));
                }
            }
            watcher.EventRecordWritten += RecordWritten;
            try
            {
                watcher.Enabled = true;
                await completion.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                watcher.Enabled = false;
                watcher.EventRecordWritten -= RecordWritten;
            }
        }
        catch (Exception exception) when (exception is not WorkerServiceException and not OperationCanceledException)
        {
            throw Failure("Unable to follow the Windows worker Application event log with the current account.", exception);
        }
    }

    internal static string WorkerQuery(long? maximumRecordId = null)
    {
        var cutoff = maximumRecordId is { } maximum
            ? " and EventRecordID <= " + maximum.ToString(CultureInfo.InvariantCulture) : "";
        return "*[System[Provider[@Name='" + WorkerServices.SERVICE_NAME + "']" + cutoff + "]]";
    }

    internal static IReadOnlyList<string> OrderHistory(IEnumerable<(DateTime? Timestamp, long? RecordId, string Message)> entries) =>
        entries.OrderBy(entry => entry.Timestamp ?? DateTime.MinValue).ThenBy(entry => entry.RecordId)
            .Select(entry => entry.Message).ToArray();

    private static (EventBookmark? Bookmark, IReadOnlyList<string> Messages) ReadHistory(CancellationToken cancellationToken)
    {
        var anchorQuery = new EventLogQuery("Application", PathType.LogName) { ReverseDirection = true };
        using var anchorReader = new EventLogReader(anchorQuery);
        using var anchor = anchorReader.ReadEvent();
        if (anchor is null)
        {
            return (null, []);
        }
        var recordId = anchor.RecordId
            ?? throw new WorkerServiceException("The Windows event log snapshot has no record identifier.");
        var bookmark = anchor.Bookmark;
        var historyQuery = new EventLogQuery("Application", PathType.LogName, WorkerQuery(recordId)) { ReverseDirection = true };
        using var reader = new EventLogReader(historyQuery);
        var history = new List<(DateTime? Timestamp, long? RecordId, string Message)>(WorkerServiceInspection.RECENT_LOG_COUNT);
        while (history.Count < WorkerServiceInspection.RECENT_LOG_COUNT)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var record = reader.ReadEvent();
            if (record is null)
            {
                break;
            }
            history.Add((record.TimeCreated, record.RecordId, FormatRecord(record)));
        }
        return (bookmark, OrderHistory(history));
    }

    private static string FormatRecord(EventRecord record)
    {
        var message = record.Properties.FirstOrDefault()?.Value as string
            ?? record.FormatDescription() ?? "Worker event " + record.Id.ToString(CultureInfo.InvariantCulture);
        var timestamp = record.TimeCreated?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)
            ?? "Unknown time";
        return "[" + timestamp + "] " + message;
    }

    private static WorkerServiceException Failure(string message, Exception exception)
    {
        var failure = new WorkerServiceException(message, exception);
        failure.Data["NativeMessage"] = exception.Message;
        failure.Data["NativeHResult"] = exception.HResult;
        if (exception is Win32Exception native)
        {
            failure.Data["NativeErrorCode"] = native.NativeErrorCode;
        }
        return failure;
    }
}
