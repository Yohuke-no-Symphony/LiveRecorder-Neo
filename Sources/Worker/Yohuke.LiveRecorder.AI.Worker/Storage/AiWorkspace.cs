using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.Worker.Common.Storage;

namespace Yohuke.LiveRecorder.AI.Worker.Storage;

internal sealed class AiWorkspace
{
    private const string DIRECTORY_NAME = "live-recorder-ai-worker";
    private readonly Lock sync = new();
    private readonly string workerId;
    private readonly string catalogPath;
    private readonly HashSet<string> roots = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly Action<string> log;

    internal AiWorkspace(string settingsPath, string workerId, Action<string> log)
    {
        this.workerId = Hash(workerId);
        this.log = log;
        catalogPath = Path.GetFullPath(settingsPath) + ".ai-checkpoints.json";
        if (File.Exists(catalogPath))
        {
            foreach (var root in WorkerJsonFileStore.Read<List<string>>(catalogPath))
            {
                if (Path.IsPathFullyQualified(root) && Path.GetFileName(root) == this.workerId && Path.GetFileName(Path.GetDirectoryName(root)) == DIRECTORY_NAME)
                {
                    roots.Add(root);
                }
            }
        }
    }

    internal string RegisterRoot(string temporaryDirectory)
    {
        var root = Path.Combine(Path.GetFullPath(temporaryDirectory), DIRECTORY_NAME, workerId);
        lock (sync)
        {
            Directory.CreateDirectory(Path.Combine(root, "jobs"));
            if (roots.Add(root))
            {
                WorkerJsonFileStore.Write(catalogPath, roots.Order(StringComparer.Ordinal).ToArray());
            }
        }
        return root;
    }

    internal AiCheckpointStore Open(AiWorkerConfiguration configuration, AiAssignment assignment)
    {
        var root = RegisterRoot(configuration.TempDirectory);
        var directory = Path.Combine(root, "jobs", Hash(assignment.JobId + "\n" + assignment.Attempt + "\n" + assignment.InputFingerprint + "\n" + assignment.ConfigurationFingerprint));
        EnsureSpace(configuration, 0);
        return new(directory, assignment);
    }

    internal IReadOnlyList<AiCheckpointStore> ReadPending()
    {
        lock (sync)
        {
            var result = new List<AiCheckpointStore>();
            foreach (var root in roots)
            {
                var jobs = Path.Combine(root, "jobs");
                if (!Directory.Exists(jobs))
                {
                    continue;
                }
                foreach (var directory in Directory.EnumerateDirectories(jobs))
                {
                    var path = Path.Combine(directory, "checkpoint.json");
                    if (!File.Exists(path))
                    {
                        continue;
                    }
                    try
                    {
                        var state = ReadCheckpoint(path);
                        if (state is not null && state.State is not ("Completed" or "Rejected"))
                        {
                            result.Add(new(directory, state.Assignment));
                        }
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
                    {
                        log("An unreadable AI checkpoint was preserved for inspection (" + error.GetType().Name + ").");
                    }
                }
            }
            return result;
        }
    }

    internal void EnsureSpace(AiWorkerConfiguration configuration, long additionalBytes)
    {
        lock (sync)
        {
            var root = RegisterRoot(configuration.TempDirectory);
            var directories = Directory.EnumerateDirectories(Path.Combine(root, "jobs"))
                .Select(path => new DirectoryInfo(path)).OrderBy(value => value.LastWriteTimeUtc).ToArray();
            var used = directories.Sum(Size);
            foreach (var directory in directories)
            {
                if (used <= configuration.MaxCacheBytes - additionalBytes && FreeBytes(root) >= configuration.MinimumFreeBytes + additionalBytes)
                {
                    return;
                }
                var path = Path.Combine(directory.FullName, "checkpoint.json");
                if (File.Exists(path) && ReadCheckpoint(path) is { State: "Completed" or "Rejected" })
                {
                    var length = Size(directory);
                    directory.Delete(true);
                    used -= length;
                }
            }
            if (used > configuration.MaxCacheBytes - additionalBytes || FreeBytes(root) < configuration.MinimumFreeBytes + additionalBytes)
            {
                throw new IOException("AI workspace capacity is unavailable; unfinished checkpoints have been retained.");
            }
        }
    }

    internal static long FreeBytes(string directory) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;

    internal static string SourcePath(string directory, string sourceId) => Path.Combine(directory, "inputs", Hash(sourceId) + ".jsonl");

    private AiWorkerCheckpoint? ReadCheckpoint(string path)
    {
        try
        {
            return WorkerJsonFileStore.Read<AiWorkerCheckpoint>(path);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            log("An unreadable AI checkpoint was preserved for inspection (" + error.GetType().Name + ").");
            return null;
        }
    }

    private static long Size(DirectoryInfo directory) => directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
