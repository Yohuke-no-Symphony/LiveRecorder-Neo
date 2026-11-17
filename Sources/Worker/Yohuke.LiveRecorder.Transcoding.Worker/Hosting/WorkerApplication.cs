using System.Net.Sockets;
using System.Security.Authentication;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Hosting;

internal static class WorkerApplication
{
    internal static async Task RegisterAsync(string configurationPath, CancellationToken cancellationToken)
    {
        using var settings = new WorkerSettingsStore(configurationPath);
        await settings.LoadAsync(cancellationToken);
        var connection = WorkerSetup.ConfigureConnection(settings.Current.Worker);
        await settings.SaveAsync(settings.Current with { Worker = connection }, cancellationToken);
        Console.WriteLine("Worker binding saved to " + settings.Path);
        var welcome = await ReadConfigurationAsync(connection, cancellationToken);
        if (string.IsNullOrWhiteSpace(welcome.Configuration.TempDirectory)
            && settings.Current.Configuration == new WorkerConfiguration())
        {
            await EditConfigurationAsync(settings, welcome, cancellationToken);
        }
        welcome = await SynchronizeAsync(settings.Current, welcome, cancellationToken);
        await settings.SaveRemoteConfigurationAsync(welcome.Configuration, welcome.ConfigurationRevision, cancellationToken);
        Console.WriteLine("Worker registered and settings synchronized with the server.");
    }

    internal static async Task ConfigureAsync(string configurationPath, CancellationToken cancellationToken)
    {
        using var settings = new WorkerSettingsStore(configurationPath);
        await settings.LoadAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.Current.Worker.WorkerId) && string.IsNullOrWhiteSpace(settings.Current.Worker.Secret))
        {
            var configuration = WorkerSetup.Configure(settings.Current.Configuration);
            WorkerSetup.Validate(configuration);
            await settings.SaveAsync(settings.Current with { Configuration = configuration }, cancellationToken);
            Console.WriteLine("Local runtime settings saved to " + settings.Path + ". Run 'register' to bind this worker.");
            return;
        }
        settings.Current.Worker.Validate();
        var welcome = await ReadConfigurationAsync(settings.Current.Worker, cancellationToken);
        await EditConfigurationAsync(settings, welcome, cancellationToken);
        welcome = await SynchronizeAsync(settings.Current, welcome, cancellationToken);
        await settings.SaveRemoteConfigurationAsync(welcome.Configuration, welcome.ConfigurationRevision, cancellationToken);
        Console.WriteLine("Runtime settings saved and synchronized with the server: " + settings.Path);
    }

    internal static async Task RunAsync(string configurationPath, CancellationToken cancellationToken, Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        using var settings = new WorkerSettingsStore(configurationPath);
        await settings.LoadAsync(cancellationToken);
        settings.Current.Worker.Validate();
        var welcome = await ConnectForRunAsync(settings.Current, log, cancellationToken);
        await settings.SaveRemoteConfigurationAsync(welcome.Configuration, welcome.ConfigurationRevision, cancellationToken);
        await new WorkerRuntime(settings.Current.Worker, settings, log).RunAsync(cancellationToken);
    }

    private static async Task EditConfigurationAsync(WorkerSettingsStore settings, WorkerWelcome welcome,
        CancellationToken cancellationToken)
    {
        var configuration = WorkerSetup.Configure(welcome.Configuration);
        WorkerSetup.Validate(configuration);
        var draft = WorkerSettingsSynchronization.Accept(settings.Current, welcome.Configuration, welcome.ConfigurationRevision)
            with { Configuration = configuration };
        await settings.SaveAsync(draft, cancellationToken);
    }

    private static async Task<WorkerWelcome> ReadConfigurationAsync(WorkerConnectionOptions options, CancellationToken cancellationToken)
    {
        await using var connection = await WorkerControlClient.ConnectAsync(options, cancellationToken);
        return await connection.ExchangeAsync<WorkerHello, WorkerWelcome>("Hello", "Welcome", new()
        {
            WorkerId = options.WorkerId,
            Secret = options.Secret
        }, cancellationToken);
    }

    private static async Task<WorkerWelcome> SynchronizeAsync(WorkerSettings settings, WorkerWelcome welcome,
        CancellationToken cancellationToken)
    {
        var update = WorkerSettingsSynchronization.CreateUpdate(settings, welcome);
        if (update is not null)
        {
            await using var connection = await WorkerControlClient.ConnectAsync(settings.Worker, cancellationToken);
            welcome = await connection.ExchangeAsync<WorkerHello, WorkerWelcome>("Hello", "Welcome", update, cancellationToken);
        }
        WorkerSetup.Validate(welcome.Configuration);
        return welcome;
    }

    private static async Task<WorkerWelcome> ConnectForRunAsync(WorkerSettings settings, Action<string> log,
        CancellationToken cancellationToken)
    {
        var reconnectDelay = 1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var welcome = await ReadConfigurationAsync(settings.Worker, cancellationToken);
                return await SynchronizeAsync(settings, welcome, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is not InvalidDataException
                && exception is IOException or SocketException or AuthenticationException or OperationCanceledException)
            {
                log("Unable to synchronize startup settings. Retrying: " + exception.Message);
                await Task.Delay(TimeSpan.FromSeconds(reconnectDelay), cancellationToken);
                reconnectDelay = Math.Min(30, reconnectDelay * 2);
            }
        }
    }
}
