using System.Text.Json;

namespace Yohuke.LiveRecorder.Worker.Common.Storage;

internal sealed class WorkerCheckpointStore<TAssignment, TPayload>
    where TAssignment : new()
    where TPayload : new()
{
    private readonly string catalogPath;
    private readonly string workerId;
    private readonly string directoryName;
    private readonly Action<string> log;
    private readonly Lock sync = new();
    private readonly HashSet<string> roots;

    internal WorkerCheckpointStore(string settingsPath, string workerId, string directoryName, Action<string> log)
    {
        this.workerId = SafeId(workerId);
        this.directoryName = SafeId(directoryName);
        this.log = log;
        catalogPath = Path.GetFullPath(settingsPath) + "." + directoryName + "-checkpoints.json";
        roots = File.Exists(catalogPath)
            ? new(WorkerJsonFileStore.Read<List<string>>(catalogPath), PathComparer)
            : new(PathComparer);
    }

    internal string RegisterRoot(string temporaryDirectory)
    {
        var root = Path.Combine(Path.GetFullPath(temporaryDirectory), directoryName, workerId);
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

    internal IReadOnlyList<WorkerCheckpointLocation<TAssignment, TPayload>> ReadAll()
    {
        lock (sync)
        {
            var result = new List<WorkerCheckpointLocation<TAssignment, TPayload>>();
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
                        var checkpoint = WorkerJsonFileStore.Read<WorkerJobCheckpoint<TAssignment, TPayload>>(path);
                        if (checkpoint.SchemaVersion != 1 || checkpoint.PipelineVersion != 1 || checkpoint.WorkerId != workerId
                            || string.IsNullOrEmpty(checkpoint.JobId) || checkpoint.Fingerprint.Length != 64
                            || checkpoint.Assignment is null || checkpoint.Payload is null || checkpoint.Stages is null)
                        {
                            throw new InvalidDataException("The checkpoint version or identity is unsupported.");
                        }
                        result.Add(new(path, checkpoint));
                    }
                    catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
                    {
                        log("Ignoring an invalid job checkpoint; its files remain available for inspection: " + error.Message);
                        try
                        {
                            File.Move(path, path + ".corrupt-" + Guid.NewGuid().ToString("N"));
                        }
                        catch (Exception quarantineError) when (quarantineError is IOException or UnauthorizedAccessException)
                        {
                            log("Unable to quarantine the invalid checkpoint: " + quarantineError.Message);
                        }
                    }
                }
            }
            return result;
        }
    }

    internal WorkerCheckpointLocation<TAssignment, TPayload> Open(string temporaryDirectory, string jobId, string fingerprint, TAssignment assignment)
    {
        var existing = ReadAll().Where(value => value.Checkpoint.JobId == jobId && value.Checkpoint.Fingerprint == fingerprint)
            .OrderByDescending(value => value.Checkpoint.LastAccessAtUtc).FirstOrDefault();
        if (existing is not null)
        {
            existing.Checkpoint.Assignment = assignment;
            existing.Checkpoint.NextLeaseToken = null;
            existing.Checkpoint.PendingResult = null;
            existing.Checkpoint.State = "Running";
            Save(existing);
            return existing;
        }
        var root = RegisterRoot(temporaryDirectory);
        var path = Path.Combine(root, "jobs", SafeId(jobId) + "-" + fingerprint, "checkpoint.json");
        var created = new WorkerCheckpointLocation<TAssignment, TPayload>(path, new()
        {
            WorkerId = workerId, JobId = jobId, Fingerprint = fingerprint, Assignment = assignment
        });
        Save(created);
        return created;
    }

    internal static void Save(WorkerCheckpointLocation<TAssignment, TPayload> location)
    {
        location.Checkpoint.LastAccessAtUtc = DateTime.UtcNow;
        WorkerJsonFileStore.Write(location.Path, location.Checkpoint);
    }

    internal static string SafeId(string value) => value.Length is > 0 and <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
        ? value : throw new InvalidDataException("Invalid worker checkpoint identity.");

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
