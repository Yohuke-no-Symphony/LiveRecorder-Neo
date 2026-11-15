namespace Yohuke.LiveRecorder.Transcoding.Worker.Services;

internal static class WorkerServiceFiles
{
    internal const string OWNER_MARKER = "Managed by Yohuke.LiveRecorder.Transcoding.Worker";

    internal static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }
        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal static FileStream AcquireOperation(string directory)
    {
        CreatePrivateDirectory(directory);
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        try
        {
            return new(Path.Combine(directory, "service-operation.lock"), options);
        }
        catch (IOException exception)
        {
            throw new WorkerServiceException("Another worker service operation is in progress, or its management lock is inaccessible.", exception);
        }
    }

    internal static async Task<string> ReadManagedAsync(string path, CancellationToken cancellationToken)
    {
        return await ReadOptionalManagedAsync(path, cancellationToken)
            ?? throw new WorkerServiceException("The worker service is not installed. Run 'install' first.");
    }

    internal static async Task<string?> ReadOptionalManagedAsync(string path, CancellationToken cancellationToken)
    {
        string content;
        try
        {
            if (new FileInfo(path).LinkTarget is not null)
            {
                throw new WorkerServiceException("The service definition is a symbolic link owned by another installation.");
            }
            content = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        if (!content.Contains(OWNER_MARKER, StringComparison.Ordinal))
        {
            throw new WorkerServiceException("A service definition with this name already exists and belongs to another installation.");
        }
        return content;
    }

    internal static async Task WriteNewAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
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
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            }
            await using (var stream = new FileStream(temporaryPath, options))
            {
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(content), cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    internal static async Task DeleteMatchingAsync(string path, string content)
    {
        if (!File.Exists(path))
        {
            return;
        }
        var current = await ReadManagedAsync(path, CancellationToken.None);
        if (current != content)
        {
            throw new WorkerServiceException("The service definition changed during this operation. Retry after checking the installed service.");
        }
        File.Delete(path);
    }
}
