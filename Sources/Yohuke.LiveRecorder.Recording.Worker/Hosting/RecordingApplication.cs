using Yohuke.LiveRecorder.Recording.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;

namespace Yohuke.LiveRecorder.Recording.Worker.Hosting;

internal static class RecordingApplication
{
    internal static async Task RegisterAsync(string path, CancellationToken cancellationToken)
    {
        using var settings = new WorkerSettingsStore(path);
        await settings.LoadAsync(cancellationToken);
        var connection = WorkerConnectionSetup.Configure(settings.Current.Worker, "Recording worker registration");
        await settings.SaveAsync(settings.Current with { Worker = connection }, cancellationToken);
        var welcome = await ReadAsync(connection, cancellationToken);
        if (string.IsNullOrWhiteSpace(welcome.Configuration.TempDirectory) && settings.Current.Configuration == new WorkerConfiguration())
        {
            await EditAsync(settings, welcome, cancellationToken);
        }
        welcome = await SynchronizeAsync(settings.Current, welcome, cancellationToken);
        await settings.SaveRemoteConfigurationAsync(welcome.Configuration, welcome.ConfigurationRevision, cancellationToken);
        Console.WriteLine("Recording worker registered. Its settings and durable room spool are retained across restarts.");
    }

    internal static async Task ConfigureAsync(string path, CancellationToken cancellationToken)
    {
        using var settings = new WorkerSettingsStore(path);
        await settings.LoadAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.Current.Worker.WorkerId) && string.IsNullOrWhiteSpace(settings.Current.Worker.Secret))
        {
            await settings.SaveAsync(settings.Current with { Configuration = RecordingSetup.Configure(settings.Current.Configuration) }, cancellationToken);
            return;
        }
        settings.Current.Worker.Validate();
        var welcome = await ReadAsync(settings.Current.Worker, cancellationToken);
        await EditAsync(settings, welcome, cancellationToken);
        welcome = await SynchronizeAsync(settings.Current, welcome, cancellationToken);
        await settings.SaveRemoteConfigurationAsync(welcome.Configuration, welcome.ConfigurationRevision, cancellationToken);
    }

    internal static async Task RunAsync(string path, CancellationToken cancellationToken, Action<string>? log = null)
    {
        using var settings = new WorkerSettingsStore(path);
        await settings.LoadAsync(cancellationToken);
        settings.Current.Worker.Validate();
        WorkerConfigurationValidation.Validate(settings.Current.Configuration, "Recording");
        await new RecordingWorkerRuntime(settings.Current.Worker, settings, log).RunAsync(cancellationToken);
    }

    private static Task EditAsync(WorkerSettingsStore settings, WorkerWelcome welcome, CancellationToken cancellationToken) =>
        settings.SaveAsync(WorkerSettingsSynchronization.Accept(settings.Current, welcome.Configuration, welcome.ConfigurationRevision)
            with { Configuration = RecordingSetup.Configure(welcome.Configuration) }, cancellationToken);

    private static async Task<WorkerWelcome> ReadAsync(WorkerConnectionOptions options, CancellationToken cancellationToken)
    {
        await using var connection = await WorkerControlClient.ConnectAsync(options, cancellationToken);
        return await connection.ExchangeAsync<WorkerHello, WorkerWelcome>("Hello", "Welcome", new()
        {
            Kind = "Recording", WorkerId = options.WorkerId, Secret = options.Secret
        }, cancellationToken);
    }

    private static async Task<WorkerWelcome> SynchronizeAsync(WorkerSettings settings, WorkerWelcome welcome, CancellationToken cancellationToken)
    {
        var update = WorkerSettingsSynchronization.CreateUpdate(settings, welcome, "Recording");
        if (update is not null)
        {
            await using var connection = await WorkerControlClient.ConnectAsync(settings.Worker, cancellationToken);
            welcome = await connection.ExchangeAsync<WorkerHello, WorkerWelcome>("Hello", "Welcome", update, cancellationToken);
        }
        WorkerConfigurationValidation.Validate(welcome.Configuration, "Recording");
        return welcome;
    }
}
