using Yohuke.LiveRecorder.Clipping.Contracts;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Worker.Common.Storage;

namespace Yohuke.LiveRecorder.Clipping.Worker.Storage;

internal sealed class ClipWorkspace(string workerId, Action<string>? log = null) : IDisposable
{
    private readonly Lock sync = new();
    private readonly Dictionary<string, FileStream> roots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> reservations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> protections = new(StringComparer.Ordinal);
    private bool disposed;

    internal ClipWorkspaceLease Acquire(WorkerConfiguration configuration, ClipAssignment assignment, string? checkpointDirectory = null)
    {
        var expected = ClipWorkspacePolicy.RequiredBytes(assignment);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var root = checkpointDirectory is null ? OpenRoot(configuration.TempDirectory)
                : OpenRootPath(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(checkpointDirectory))!)!);
            var directory = checkpointDirectory ?? Path.Combine(root, "jobs", SafeId(assignment.JobId) + "-" + assignment.CheckpointGeneration);
            if (assignment.Attempt <= 0 || reservations.ContainsKey(directory))
            {
                throw new InvalidOperationException("The same clipping attempt cannot acquire its workspace twice.");
            }
            var existing = Size(root);
            var reserved = reservations.Where(item => IsWithin(item.Key, root))
                .Sum(item => Math.Max(0, item.Value - Size(item.Key)));
            var drive = Drive(root);
            var driveReserved = reservations.Where(item => Drive(item.Key).Name == drive.Name)
                .Sum(item => Math.Max(0, item.Value - Size(item.Key)));
            var remaining = Math.Max(0, expected - Size(directory));
            foreach (var candidate in Directory.EnumerateDirectories(Path.Combine(root, "jobs"))
                .Where(path => path != directory && !reservations.ContainsKey(path) && !protections.ContainsKey(path)).OrderBy(Directory.GetLastWriteTimeUtc))
            {
                if (remaining <= configuration.MaxCacheBytes - existing - reserved
                    && remaining <= drive.AvailableFreeSpace - driveReserved - configuration.MinimumFreeBytes)
                {
                    break;
                }
                var bytes = Size(candidate);
                Cleanup(candidate);
                existing -= bytes;
            }
            if (remaining > configuration.MaxCacheBytes - existing - reserved
                || remaining > drive.AvailableFreeSpace - driveReserved - configuration.MinimumFreeBytes)
            {
                throw new ClipCapacityException("Insufficient clipping workspace capacity or free disk space.");
            }
            Directory.CreateDirectory(directory);
            reservations.Add(directory, expected);
            return new(directory, completed => Release(directory, completed));
        }
    }

    internal long FreeBytes(string temporaryDirectory)
    {
        lock (sync)
        {
            return Drive(OpenRoot(temporaryDirectory)).AvailableFreeSpace;
        }
    }

    private string OpenRoot(string temporaryDirectory)
    {
        if (!Path.IsPathFullyQualified(temporaryDirectory))
        {
            throw new InvalidDataException("The clipping temporary directory must be absolute.");
        }
        return OpenRootPath(Path.Combine(Path.GetFullPath(temporaryDirectory), "live-recorder-clipping-worker", SafeId(workerId)));
    }

    internal void OwnCheckpointDirectory(string directory)
    {
        lock (sync)
        {
            _ = OpenRootPath(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(directory))!)!);
        }
    }

    internal IDisposable ProtectCheckpointDirectory(string directory)
    {
        OwnCheckpointDirectory(directory);
        lock (sync)
        {
            protections[directory] = protections.GetValueOrDefault(directory) + 1;
        }
        return new WorkerWorkspaceProtection(() =>
        {
            lock (sync)
            {
                if (protections.GetValueOrDefault(directory) <= 1)
                {
                    protections.Remove(directory);
                }
                else
                {
                    protections[directory]--;
                }
            }
        });
    }

    private string OpenRootPath(string root)
    {
        if (roots.ContainsKey(root))
        {
            return root;
        }
        Directory.CreateDirectory(root);
        var owner = new FileStream(Path.Combine(root, ".worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var jobs = Path.Combine(root, "jobs");
            Directory.CreateDirectory(jobs);
            roots.Add(root, owner);
            return root;
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    private void Release(string directory, bool completed)
    {
        lock (sync)
        {
            if (completed)
            {
                Cleanup(directory);
            }
            reservations.Remove(directory);
        }
    }

    private void Cleanup(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log?.Invoke("Clipping workspace cleanup will be retried after restart: " + error.Message);
        }
    }

    private static long Size(string directory)
    {
        var total = 0L;
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            try
            {
                foreach (var path in Directory.GetFiles(current))
                {
                    try
                    {
                        total = checked(total + new FileInfo(path).Length);
                    }
                    catch (FileNotFoundException)
                    {
                    }
                }
                foreach (var child in Directory.GetDirectories(current))
                {
                    pending.Push(child);
                }
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
        return total;
    }

    private static DriveInfo Drive(string directory)
    {
        var full = Path.GetFullPath(directory);
        return DriveInfo.GetDrives().Where(drive => drive.IsReady && IsWithin(full, drive.RootDirectory.FullName))
            .MaxBy(drive => drive.RootDirectory.FullName.Length)
            ?? throw new IOException("Unable to determine clipping workspace disk capacity.");
    }

    private static bool IsWithin(string path, string directory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        return path.Equals(directory, comparison) || path.StartsWith(prefix, comparison);
    }

    private static string SafeId(string id) => id.Length is > 0 and <= 128
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
        ? id : throw new InvalidDataException("Invalid clipping job or worker identity.");

    /// <inheritdoc />
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            reservations.Clear();
            foreach (var owner in roots.Values)
            {
                owner.Dispose();
            }
            roots.Clear();
        }
    }
}
