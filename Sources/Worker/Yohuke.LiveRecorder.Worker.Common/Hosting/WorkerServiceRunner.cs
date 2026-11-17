using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using Yohuke.LiveRecorder.Transcoding.Worker.Services;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Hosting;

internal static class WorkerServiceRunner
{
    internal static Task RunAsync(string configurationPath, WorkerServiceIdentity identity,
        Func<string, CancellationToken, Action<string>?, Task> run, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsWorkerProcessJob.AttachCurrentProcess();
        }
        if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
        {
            return RunWindowsServiceAsync(configurationPath, identity, run, cancellationToken);
        }
        return run(configurationPath, cancellationToken, null);
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunWindowsServiceAsync(string configurationPath, WorkerServiceIdentity identity,
        Func<string, CancellationToken, Action<string>?, Task> run, CancellationToken cancellationToken)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Services.AddWindowsService(options => options.ServiceName = identity.ServiceName);
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(60));
        builder.Services.Configure<EventLogSettings>(options =>
        {
            options.SourceName = identity.ServiceName;
            options.LogName = "Application";
            options.Filter = (_, level) => level >= LogLevel.Information;
        });
        builder.Services.AddHostedService(services => new WorkerHostedService(configurationPath, run,
            services.GetRequiredService<IHostApplicationLifetime>(), services.GetRequiredService<ILogger<WorkerHostedService>>()));
        using var host = builder.Build();
        await host.RunAsync(cancellationToken);
    }
}
