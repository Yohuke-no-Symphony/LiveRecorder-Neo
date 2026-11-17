using System.Net.Sockets;
using System.Security.Authentication;
using Yohuke.LiveRecorder.AI.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;

namespace Yohuke.LiveRecorder.AI.Worker.Hosting;

internal static class AiApplication
{
    internal static async Task RegisterAsync(string path, CancellationToken cancellationToken)
    {
        using var settings = new WorkerSettingsStore(path, "AI");
        await settings.LoadAsync(cancellationToken);
        var connection = WorkerConnectionSetup.Configure(settings.Current.Worker, "AI worker registration");
        await settings.SaveAsync(settings.Current with { Worker = connection }, cancellationToken);
        Console.WriteLine("Worker binding saved to " + settings.Path);
        var welcome = await ReadAsync(connection, cancellationToken);
        if (string.IsNullOrWhiteSpace(welcome.Configuration.TempDirectory)
            && string.IsNullOrWhiteSpace(settings.Current.Configuration.TempDirectory))
        {
            await EditAsync(settings, welcome, cancellationToken);
        }
        welcome = await SynchronizeAsync(settings.Current, welcome, cancellationToken);
        await settings.SaveRemoteConfigurationAsync(welcome.Configuration, welcome.ConfigurationRevision, cancellationToken);
        Console.WriteLine("AI worker registered and settings synchronized with the server.");
    }

    internal static async Task ConfigureAsync(string path, CancellationToken cancellationToken)
    {
        using var settings = new WorkerSettingsStore(path, "AI");
        await settings.LoadAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.Current.Worker.WorkerId) && string.IsNullOrWhiteSpace(settings.Current.Worker.Secret))
        {
            await settings.SaveAsync(settings.Current with { Configuration = AiSetup.Configure(settings.Current.Configuration) }, cancellationToken);
            Console.WriteLine("Local AI settings saved. Run 'register' to bind this worker.");
            return;
        }
        settings.Current.Worker.Validate();
        var welcome = await ReadAsync(settings.Current.Worker, cancellationToken);
        await EditAsync(settings, welcome, cancellationToken);
        welcome = await SynchronizeAsync(settings.Current, welcome, cancellationToken);
        await settings.SaveRemoteConfigurationAsync(welcome.Configuration, welcome.ConfigurationRevision, cancellationToken);
        Console.WriteLine("AI settings synchronized with the server: " + settings.Path);
    }

    internal static async Task RunAsync(string path, CancellationToken cancellationToken, Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        using var settings = new WorkerSettingsStore(path, "AI");
        await settings.LoadAsync(cancellationToken);
        settings.Current.Worker.Validate();
        var delay = 1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var welcome = await ReadAsync(settings.Current.Worker, cancellationToken);
                welcome = await SynchronizeAsync(settings.Current, welcome, cancellationToken);
                await settings.SaveRemoteConfigurationAsync(welcome.Configuration, welcome.ConfigurationRevision, cancellationToken);
                break;
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is not WorkerAuthenticationException
                && error is IOException or SocketException or AuthenticationException or OperationCanceledException)
            {
                log("Unable to synchronize AI settings. Retrying: " + error.Message);
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                delay = Math.Min(30, delay * 2);
            }
        }
        await new AiWorkerRuntime(settings.Current.Worker, settings, log).RunAsync(cancellationToken);
    }

    private static Task EditAsync(WorkerSettingsStore settings, WorkerWelcome welcome, CancellationToken cancellationToken)
    {
        var draft = WorkerSettingsSynchronization.Accept(settings.Current, welcome.Configuration, welcome.ConfigurationRevision, "AI")
            with { Configuration = AiSetup.Configure(welcome.Configuration) };
        return settings.SaveAsync(draft, cancellationToken);
    }

    private static async Task<WorkerWelcome> ReadAsync(WorkerConnectionOptions options, CancellationToken cancellationToken)
    {
        await using var connection = await WorkerControlClient.ConnectAsync(options, cancellationToken);
        return await connection.ExchangeAsync<WorkerHello, WorkerWelcome>("Hello", "Welcome", new()
        {
            Kind = "AI", WorkerId = options.WorkerId, Secret = options.Secret
        }, cancellationToken);
    }

    private static async Task<WorkerWelcome> SynchronizeAsync(WorkerSettings settings, WorkerWelcome welcome, CancellationToken cancellationToken)
    {
        var update = WorkerSettingsSynchronization.CreateUpdate(settings, welcome, "AI");
        if (update is not null)
        {
            await using var connection = await WorkerControlClient.ConnectAsync(settings.Worker, cancellationToken);
            welcome = await connection.ExchangeAsync<WorkerHello, WorkerWelcome>("Hello", "Welcome", update, cancellationToken);
        }
        WorkerConfigurationValidation.Validate(welcome.Configuration, "AI");
        return welcome;
    }
}
