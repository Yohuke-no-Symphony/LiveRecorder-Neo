using System.ComponentModel;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Win32;
using Yohuke.LiveRecorder.Transcoding.Worker.CommandLine;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Services;

[SupportedOSPlatform("windows")]
internal static class WindowsWorkerService
{
    private const string DISPLAY_NAME = "Yohuke Live Recorder Transcoding Worker";
    private const string REGISTRY_PATH = @"SYSTEM\CurrentControlSet\Services\" + WorkerServices.SERVICE_NAME;
    private static string ServiceControllerPath => Path.Combine(Environment.SystemDirectory, "sc.exe");

    internal static async Task ExecuteAsync(WorkerCommand command, string configurationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (command)
        {
            case WorkerCommand.Install:
                await InstallAsync(configurationPath, cancellationToken);
                break;
            case WorkerCommand.Uninstall:
                await UninstallAsync(cancellationToken);
                break;
            case WorkerCommand.Start:
            case WorkerCommand.Stop:
                RequireOwnedService();
                await ChangeRunningStateAsync(command, cancellationToken);
                break;
            case WorkerCommand.Enable:
            case WorkerCommand.Disable:
                RequireOwnedService();
                await WorkerServiceProcess.ExecuteAsync(ServiceControllerPath,
                    ["config", WorkerServices.SERVICE_NAME, "start=", command == WorkerCommand.Enable ? "auto" : "demand"],
                    "change worker service startup", cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    private static async Task InstallAsync(string configurationPath, CancellationToken cancellationToken)
    {
        var imagePath = WorkerServiceInvocation.Current(configurationPath).WindowsCommandLine();
        var existing = ReadRegistration();
        if (existing is { } registration)
        {
            if (!MatchesInvocation(registration, imagePath)
                || !string.IsNullOrEmpty(registration.Description) && registration.Description != WorkerServiceFiles.OWNER_MARKER)
            {
                throw new WorkerServiceException("A service with this name is already installed with a different executable, configuration, or owner. Uninstall it before installing this worker.");
            }
            try
            {
                using var service = new ServiceController(WorkerServices.SERVICE_NAME);
                _ = service.Status;
            }
            catch (Exception exception) when (NativeError(exception) is 1060 or 1072)
            {
                var nativeError = NativeError(exception);
                var failure = new WorkerServiceException(nativeError == 1072
                    ? "The worker service is marked for deletion. Close open service management windows, then retry 'uninstall' or 'install'."
                    : "Windows still has a worker service registration whose service no longer exists. Wait for service removal to finish, then retry 'install'.", exception);
                failure.Data["NativeErrorCode"] = nativeError;
                failure.Data["NativeMessage"] = exception.Message;
                throw failure;
            }
            if (registration.Description == WorkerServiceFiles.OWNER_MARKER)
            {
                return;
            }
            await SetDescriptionAsync(cancellationToken);
            return;
        }

        var created = false;
        var creationInProgress = false;
        try
        {
            creationInProgress = true;
            var result = await WorkerServiceProcess.RunAsync(ServiceControllerPath,
                ["create", WorkerServices.SERVICE_NAME, "binPath=", imagePath, "type=", "own", "start=", "demand",
                    "obj=", "LocalSystem", "DisplayName=", DISPLAY_NAME], cancellationToken);
            creationInProgress = false;
            WorkerServiceProcess.RequireSuccess(result, "install the worker service");
            created = true;
            await SetDescriptionAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            if (created || creationInProgress)
            {
                try
                {
                    var current = ReadRegistration();
                    if (current is { } candidate && MatchesInvocation(candidate, imagePath)
                        && (string.IsNullOrEmpty(candidate.Description) || candidate.Description == WorkerServiceFiles.OWNER_MARKER))
                    {
                        await ChangeRunningStateAsync(WorkerCommand.Stop, CancellationToken.None);
                        await DeleteAsync(CancellationToken.None);
                    }
                }
                catch (Exception rollbackException)
                {
                    exception.Data["RollbackError"] = rollbackException.Message;
                }
            }
            throw;
        }
    }

    private static Task SetDescriptionAsync(CancellationToken cancellationToken) =>
        WorkerServiceProcess.ExecuteAsync(ServiceControllerPath,
            ["description", WorkerServices.SERVICE_NAME, WorkerServiceFiles.OWNER_MARKER],
            "set worker service ownership", cancellationToken);

    private static async Task UninstallAsync(CancellationToken cancellationToken)
    {
        if (ReadRegistration() is null)
        {
            return;
        }
        RequireOwnedService();
        try
        {
            await ChangeRunningStateAsync(WorkerCommand.Stop, cancellationToken);
        }
        catch (InvalidOperationException exception) when (NativeError(exception) is 1060 or 1072)
        {
        }
        await DeleteAsync(cancellationToken);
    }

    private static async Task DeleteAsync(CancellationToken cancellationToken)
    {
        var result = await WorkerServiceProcess.RunAsync(ServiceControllerPath,
            ["delete", WorkerServices.SERVICE_NAME], cancellationToken);
        if (result.ExitCode is not (0 or 1060 or 1072))
        {
            WorkerServiceProcess.RequireSuccess(result, "remove the worker service");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        try
        {
            while (ReadRegistration() is not null)
            {
                await Task.Delay(200, timeout.Token);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WorkerServiceException("Timed out waiting for the worker service to be removed. Close open service management windows and retry 'uninstall'.", exception);
        }
    }

    private static async Task ChangeRunningStateAsync(WorkerCommand command, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        using var service = new ServiceController(WorkerServices.SERVICE_NAME);
        try
        {
            if (command == WorkerCommand.Start)
            {
                await StartAsync(service, timeout.Token);
            }
            else
            {
                await StopAsync(service, timeout.Token);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WorkerServiceException(command == WorkerCommand.Start
                ? "Timed out waiting for the worker service to start."
                : "Timed out waiting for the worker service to stop.", exception);
        }
    }

    private static async Task StartAsync(ServiceController service, CancellationToken cancellationToken)
    {
        service.Refresh();
        if (service.Status == ServiceControllerStatus.StopPending)
        {
            await WaitForStatusAsync(service, ServiceControllerStatus.Stopped, cancellationToken);
        }
        if (service.Status == ServiceControllerStatus.Running)
        {
            return;
        }
        if (service.Status == ServiceControllerStatus.Stopped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                service.Start();
            }
            catch (InvalidOperationException exception) when (NativeError(exception) == 1056)
            {
            }
        }
        else if (service.Status != ServiceControllerStatus.StartPending)
        {
            throw new WorkerServiceException("The worker service cannot start in its current state. Stop it before starting it again.");
        }
        await WaitForStatusAsync(service, ServiceControllerStatus.Running, cancellationToken);
    }

    private static async Task StopAsync(ServiceController service, CancellationToken cancellationToken)
    {
        service.Refresh();
        while (service.Status is ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending or ServiceControllerStatus.PausePending)
        {
            await Task.Delay(200, cancellationToken);
            service.Refresh();
        }
        if (service.Status == ServiceControllerStatus.Stopped)
        {
            return;
        }
        if (service.Status != ServiceControllerStatus.StopPending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                service.Stop();
            }
            catch (InvalidOperationException exception) when (NativeError(exception) == 1062)
            {
            }
        }
        await WaitForStatusAsync(service, ServiceControllerStatus.Stopped, cancellationToken);
    }

    private static async Task WaitForStatusAsync(ServiceController service, ServiceControllerStatus expected,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            service.Refresh();
            if (service.Status == expected)
            {
                return;
            }
            if (expected == ServiceControllerStatus.Running && service.Status == ServiceControllerStatus.Stopped)
            {
                throw new WorkerServiceException("The worker service stopped before becoming ready. Check its Windows event log.");
            }
            await Task.Delay(200, cancellationToken);
        }
    }

    private static void RequireOwnedService()
    {
        var registration = ReadRegistration()
            ?? throw new WorkerServiceException("The worker service is not installed. Run 'install' first.");
        if (registration.Description != WorkerServiceFiles.OWNER_MARKER || registration.DisplayName != DISPLAY_NAME)
        {
            throw new WorkerServiceException("A service with this name belongs to another installation.");
        }
    }

    private static bool MatchesInvocation((string? ImagePath, string? Description, string? DisplayName) registration, string imagePath) =>
        string.Equals(registration.ImagePath, imagePath, StringComparison.OrdinalIgnoreCase) && registration.DisplayName == DISPLAY_NAME;

    private static (string? ImagePath, string? Description, string? DisplayName)? ReadRegistration()
    {
        using var key = Registry.LocalMachine.OpenSubKey(REGISTRY_PATH);
        if (key is null)
        {
            return null;
        }
        return (key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
            key.GetValue("Description", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
            key.GetValue("DisplayName", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
    }

    private static int? NativeError(Exception exception) =>
        (exception as Win32Exception ?? exception.InnerException as Win32Exception)?.NativeErrorCode;
}
