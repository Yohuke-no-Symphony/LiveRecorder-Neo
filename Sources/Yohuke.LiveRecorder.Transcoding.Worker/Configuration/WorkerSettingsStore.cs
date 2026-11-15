using System.Text.Json;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

internal sealed class WorkerSettingsStore : IDisposable
{
    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly FileStream lease;

    internal WorkerSettingsStore(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        lease = new(Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal string Path { get; }
    internal WorkerSettings Current { get; private set; } = new();

    internal async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var settings = await JsonSerializer.DeserializeAsync<WorkerSettings>(stream, JsonOptions, cancellationToken);
            if (settings is null || settings.Worker is null || settings.Configuration is null || settings.ConfigurationRevision < 0)
            {
                throw new InvalidDataException("settings.json must contain valid Worker and Configuration objects and a nonnegative configuration revision.");
            }
            Current = settings;
        }
        catch (FileNotFoundException)
        {
            Current = new();
        }
    }

    internal async Task SaveAsync(WorkerSettings settings, CancellationToken cancellationToken)
    {
        var temporaryPath = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            await using (var stream = new FileStream(temporaryPath, options))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, Path, true);
            Current = settings;
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    internal Task SaveRemoteConfigurationAsync(WorkerConfiguration configuration, int revision, CancellationToken cancellationToken)
    {
        var updated = WorkerSettingsSynchronization.Accept(Current, configuration, revision);
        return updated == Current ? Task.CompletedTask : SaveAsync(updated, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose() => lease.Dispose();
}
