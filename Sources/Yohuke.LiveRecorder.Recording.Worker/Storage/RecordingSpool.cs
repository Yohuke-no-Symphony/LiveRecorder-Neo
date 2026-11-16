using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Recording.Contracts;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Recording.Worker.Storage;

internal sealed class RecordingSpool : IDisposable
{
    private readonly Lock sync = new();
    private readonly string registryPath;
    private readonly string workerId;
    private readonly Dictionary<string, FileStream> roots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> assignmentPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> pinnedEventSnapshots = new(StringComparer.Ordinal);
    private readonly Action<string> log;

    internal RecordingSpool(string settingsPath, string workerId, string temporaryDirectory, Action<string>? log = null)
    {
        registryPath = settingsPath + ".recording-spools.json";
        this.workerId = RecordingSpoolFiles.SafeId(workerId);
        this.log = log ?? Console.WriteLine;
        try
        {
            if (File.Exists(registryPath))
            {
                foreach (var root in RecordingSpoolFiles.Read<List<string>>(registryPath))
                {
                    OpenRoot(root);
                }
            }
            EnsureRoot(temporaryDirectory);
            foreach (var root in roots.Keys)
            {
                foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "assignments"), "*.json"))
                {
                    var state = RecordingSpoolFiles.Read<RecordingAssignmentState>(path);
                    if (state.Assignment.WorkerId != workerId)
                    {
                        throw new InvalidDataException("A recording spool belongs to a different Worker.");
                    }
                    assignmentPaths[state.Assignment.AssignmentId] = path;
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal string EnsureRoot(string temporaryDirectory)
    {
        if (!Path.IsPathFullyQualified(temporaryDirectory))
        {
            throw new InvalidDataException("The persistent recording spool directory must be absolute.");
        }
        lock (sync)
        {
            var root = Path.Combine(Path.GetFullPath(temporaryDirectory), "live-recorder-recording-worker", workerId);
            var added = !roots.ContainsKey(root);
            OpenRoot(root);
            if (added)
            {
                RecordingSpoolFiles.Write(registryPath, roots.Keys.ToList());
            }
            return root;
        }
    }

    private void OpenRoot(string root)
    {
        if (roots.ContainsKey(root))
        {
            return;
        }
        Directory.CreateDirectory(root);
        var owner = new FileStream(Path.Combine(root, ".worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "assignments"));
            Directory.CreateDirectory(Path.Combine(root, "sessions"));
            roots.Add(root, owner);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    internal IReadOnlyList<RecordingAssignmentState> Assignments()
    {
        lock (sync)
        {
            return assignmentPaths.Values.Select(RecordingSpoolFiles.Read<RecordingAssignmentState>).ToArray();
        }
    }

    internal void SaveAssignment(RecordingAssignment assignment, string temporaryDirectory, bool released = false)
    {
        if (assignment.WorkerId != workerId || assignment.Generation <= 0 || string.IsNullOrWhiteSpace(assignment.Token))
        {
            throw new InvalidDataException("The recording assignment has an invalid durable owner.");
        }
        lock (sync)
        {
            if (!assignmentPaths.TryGetValue(assignment.AssignmentId, out var path))
            {
                path = Path.Combine(EnsureRoot(temporaryDirectory), "assignments", RecordingSpoolFiles.SafeId(assignment.AssignmentId) + ".json");
            }
            var previous = File.Exists(path) ? RecordingSpoolFiles.Read<RecordingAssignmentState>(path) : new();
            RecordingSpoolFiles.Write(path, previous with { Assignment = assignment, Released = released });
            assignmentPaths[assignment.AssignmentId] = path;
        }
    }

    internal RecordingSpoolSession CreateSession(RecordingAssignment assignment, RecordingSessionDescriptor descriptor,
        string temporaryDirectory)
    {
        lock (sync)
        {
            var directory = Path.Combine(EnsureRoot(temporaryDirectory), "sessions", RecordingSpoolFiles.SafeId(descriptor.SessionId));
            var state = new RecordingSessionState { AssignmentId = assignment.AssignmentId, Descriptor = descriptor };
            RecordingSpoolFiles.Write(Path.Combine(directory, "session.json"), state);
            return new(directory, state);
        }
    }

    internal IReadOnlyList<RecordingSpoolSession> Sessions(string? assignmentId = null)
    {
        lock (sync)
        {
            var result = new List<RecordingSpoolSession>();
            foreach (var root in roots.Keys)
            {
                foreach (var directory in Directory.EnumerateDirectories(Path.Combine(root, "sessions")))
                {
                    var path = Path.Combine(directory, "session.json");
                    if (File.Exists(path))
                    {
                        var state = RecordingSpoolFiles.Read<RecordingSessionState>(path);
                        if (assignmentId is null || state.AssignmentId == assignmentId)
                        {
                            result.Add(new(directory, state));
                        }
                    }
                }
            }
            return result;
        }
    }

    internal RecordingSessionState UpdateSession(string directory, Action<RecordingSessionState> update)
    {
        lock (sync)
        {
            var path = Path.Combine(directory, "session.json");
            var state = RecordingSpoolFiles.Read<RecordingSessionState>(path);
            update(state);
            RecordingSpoolFiles.Write(path, state);
            return state;
        }
    }

    internal void RecordMedia(string directory, MediaRecordingUpdate update)
    {
        if (update.CommitSnapshot is { DurationMs: > 0 } snapshot)
        {
            UpdateSession(directory, state => state.LastCapturedAtUtc = state.Descriptor.StartedAtUtc
                .AddMilliseconds(snapshot.StartOffsetMs + snapshot.DurationMs));
        }
    }

    internal async Task RecordEventsAsync(string directory, EventArchiveCommit commit)
    {
        var eventsDirectory = Path.Combine(directory, "弹幕");
        var snapshots = Path.Combine(eventsDirectory, "snapshots");
        Directory.CreateDirectory(snapshots);
        var temporary = Path.Combine(snapshots, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                if (commit.IndexCommittedBytes > 0)
                {
                    await using var input = new FileStream(Path.Combine(eventsDirectory, "事件.index.jsonl"), FileMode.Open,
                        FileAccess.Read, FileShare.ReadWrite, 65536, true);
                    var remaining = commit.IndexCommittedBytes;
                    var buffer = new byte[65536];
                    while (remaining > 0)
                    {
                        var count = (int)Math.Min(remaining, buffer.Length);
                        await input.ReadExactlyAsync(buffer.AsMemory(0, count));
                        await output.WriteAsync(buffer.AsMemory(0, count));
                        remaining -= count;
                    }
                }
                if (commit.PendingBlock is { } pending)
                {
                    await output.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pending, EventArchiveJson.Options) + "\n"));
                }
                await output.FlushAsync();
                output.Flush(true);
            }
            string hash;
            await using (var input = File.OpenRead(temporary))
            {
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input));
            }
            var destination = Path.Combine(snapshots, hash + ".jsonl");
            var bytes = new FileInfo(temporary).Length;
            lock (sync)
            {
                File.Move(temporary, destination, true);
                bytes = new FileInfo(destination).Length;
                UpdateSession(directory, state =>
                {
                    state.Events = new()
                    {
                        CommittedBytes = commit.CommittedBytes, IndexCommittedBytes = bytes,
                        ThroughSequence = commit.ThroughSequence, IndexSha256 = hash,
                        IsClosed = state.Descriptor.IsClosed
                    };
                    state.EventIndexPath = Path.GetRelativePath(directory, destination);
                });
                PruneEventSnapshots(directory);
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal IReadOnlyList<MediaRecordingCommitSnapshot> Commits(string directory)
    {
        var root = Path.Combine(directory, "录制提交");
        return Directory.Exists(root) ? Directory.EnumerateFiles(root, "latest.json", SearchOption.AllDirectories)
            .Select(RecordingSpoolFiles.Read<MediaRecordingCommitSnapshot>).ToArray() : [];
    }

    internal RecordingEventSnapshotLease PinEvents(string directory)
    {
        lock (sync)
        {
            var state = RecordingSpoolFiles.Read<RecordingSessionState>(Path.Combine(directory, "session.json"));
            var path = state.EventIndexPath is { } relative ? RecordingSpoolFiles.Resolve(directory, relative) : null;
            if (path is not null)
            {
                pinnedEventSnapshots[path] = pinnedEventSnapshots.GetValueOrDefault(path) + 1;
            }
            return new(new(directory, state), () =>
            {
                lock (sync)
                {
                    if (path is not null && --pinnedEventSnapshots[path] == 0)
                    {
                        pinnedEventSnapshots.Remove(path);
                    }
                    if (Directory.Exists(directory))
                    {
                        PruneEventSnapshots(directory);
                    }
                }
            });
        }
    }

    private void PruneEventSnapshots(string directory)
    {
        var state = RecordingSpoolFiles.Read<RecordingSessionState>(Path.Combine(directory, "session.json"));
        var current = state.EventIndexPath is { } relative ? RecordingSpoolFiles.Resolve(directory, relative) : null;
        var snapshots = Path.Combine(directory, "弹幕", "snapshots");
        if (!Directory.Exists(snapshots))
        {
            return;
        }
        foreach (var path in Directory.EnumerateFiles(snapshots, "*.jsonl"))
        {
            if (path != current && !pinnedEventSnapshots.ContainsKey(path))
            {
                File.Delete(path);
            }
        }
    }

    internal void CloseSession(string directory, DateTime endedAtUtc)
    {
        UpdateSession(directory, state =>
        {
            state.Descriptor = state.Descriptor with { IsClosed = true, EndedAtUtc = endedAtUtc };
            state.Events = (state.Events ?? new RecordingEventCommit
            {
                IndexSha256 = Convert.ToHexStringLower(SHA256.HashData([]))
            }) with { IsClosed = true };
        });
    }

    internal async Task RecoverAsync(CancellationToken cancellationToken)
    {
        foreach (var session in Sessions())
        {
            foreach (var commit in Commits(session.DirectoryPath).Where(value => !value.IsClosed))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var media = RecordingSpoolFiles.Resolve(session.DirectoryPath, commit.RelativePath);
                var index = RecordingSpoolFiles.Resolve(session.DirectoryPath, commit.IndexRelativePath);
                var prefix = RecordingSpoolFiles.Resolve(session.DirectoryPath, commit.PrefixRelativePath);
                var prefixBytes = await File.ReadAllBytesAsync(prefix, cancellationToken);
                if (prefixBytes.Length != commit.PrefixLength || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(prefixBytes)),
                        commit.PrefixSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("A recording recovery prefix is missing or corrupt; the spool has been preserved.");
                }
                await using (var output = new FileStream(media, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    if (output.Length < commit.MediaCommittedBytes)
                    {
                        throw new InvalidDataException("A recording recovery file is shorter than its durable commit.");
                    }
                    output.SetLength(commit.MediaCommittedBytes);
                    await output.WriteAsync(prefixBytes, cancellationToken);
                    output.Flush(true);
                }
                using (var output = new FileStream(index, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    if (output.Length < commit.IndexCommittedBytes)
                    {
                        throw new InvalidDataException("A recording recovery index is shorter than its durable commit.");
                    }
                    output.SetLength(commit.IndexCommittedBytes);
                    output.Flush(true);
                }
                RecordingSpoolFiles.Write(Path.Combine(session.DirectoryPath, "录制提交", commit.SegmentId, "latest.json"),
                    commit with { CommitSequence = checked(commit.CommitSequence + 1), IsClosed = true });
                log("Recovered a recording segment at its last durable media and index boundary.");
            }
            var events = Path.Combine(session.DirectoryPath, "弹幕", "事件.jsonl");
            if (File.Exists(events))
            {
                using (var output = new FileStream(events, FileMode.Open, FileAccess.Write, FileShare.None))
                {
                    var committed = session.State.Events?.CommittedBytes ?? 0;
                    if (output.Length < committed)
                    {
                        throw new InvalidDataException("A recording event archive is shorter than its durable commit.");
                    }
                    output.SetLength(committed);
                    output.Flush(true);
                }
                await using var writer = new SessionEventWriter(Path.GetDirectoryName(events)!,
                    commit => RecordEventsAsync(session.DirectoryPath, commit));
            }
        }
    }

    internal long Bytes()
    {
        lock (sync)
        {
            return roots.Keys.Sum(Size);
        }
    }

    internal long PendingBytes()
    {
        var result = 0L;
        foreach (var session in Sessions())
        {
            var receiptPath = Path.Combine(session.DirectoryPath, "uploaded.json");
            var receipt = File.Exists(receiptPath) ? RecordingSpoolFiles.Read<RecordingUploadReceipt>(receiptPath) : new();
            foreach (var commit in Commits(session.DirectoryPath))
            {
                receipt.Segments.TryGetValue(commit.SegmentId, out var accepted);
                result = checked(result + Math.Max(0, commit.MediaCommittedBytes - (accepted?.MediaCommittedBytes ?? 0))
                    + Math.Max(0, commit.IndexCommittedBytes - (accepted?.IndexCommittedBytes ?? 0)));
            }
            result = checked(result + Math.Max(0, (session.State.Events?.CommittedBytes ?? 0) - (receipt.Events?.CommittedBytes ?? 0)));
        }
        return result;
    }

    internal long FreeBytes(string temporaryDirectory)
    {
        var root = EnsureRoot(temporaryDirectory);
        return DriveInfo.GetDrives().Where(value => value.IsReady && root.StartsWith(value.RootDirectory.FullName,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .MaxBy(value => value.RootDirectory.FullName.Length)?.AvailableFreeSpace ?? 0;
    }

    internal bool HasCapacity(WorkerConfiguration configuration, string? sessionDirectory = null)
    {
        lock (sync)
        {
            var root = EnsureRoot(configuration.TempDirectory);
            if (sessionDirectory is not null)
            {
                root = roots.Keys.Where(path => sessionDirectory.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    .MaxBy(path => path.Length) ?? root;
            }
            var drive = DriveInfo.GetDrives().Where(value => value.IsReady && Path.GetFullPath(root)
                    .StartsWith(value.RootDirectory.FullName, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                .MaxBy(value => value.RootDirectory.FullName.Length);
            return drive is not null && drive.AvailableFreeSpace > configuration.MinimumFreeBytes
                && roots.Keys.Sum(Size) < configuration.MaxCacheBytes;
        }
    }

    private static long Size(string directory)
    {
        var result = 0L;
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            try
            {
                foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos("*", new EnumerationOptions
                         { AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    if (entry is DirectoryInfo child)
                    {
                        pending.Push(child.FullName);
                    }
                    else if (entry is FileInfo file)
                    {
                        result = checked(result + file.Length);
                    }
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
        return result;
    }

    internal void DeleteAcknowledgedSession(string directory)
    {
        lock (sync)
        {
            var session = RecordingSpoolFiles.Read<RecordingSessionState>(Path.Combine(directory, "session.json"));
            var receiptPath = Path.Combine(directory, "uploaded.json");
            if (!session.Descriptor.IsClosed || !File.Exists(receiptPath)
                || !RecordingSpoolFiles.Read<RecordingUploadReceipt>(receiptPath).SessionClosed)
            {
                throw new InvalidOperationException("Unacknowledged recording session data must remain in the spool.");
            }
            var assignmentPath = assignmentPaths[session.AssignmentId];
            var assignment = RecordingSpoolFiles.Read<RecordingAssignmentState>(assignmentPath);
            if (!assignment.CompletedSessionIds.Contains(session.Descriptor.SessionId))
            {
                RecordingSpoolFiles.Write(assignmentPath, assignment with
                {
                    CompletedSessionIds = [.. assignment.CompletedSessionIds, session.Descriptor.SessionId]
                });
            }
            Directory.Delete(directory, true);
        }
    }

    internal void QuarantineDraft(string directory)
    {
        lock (sync)
        {
            var root = Directory.GetParent(Path.GetDirectoryName(directory)!)!.FullName;
            var quarantine = Path.Combine(root, "drafts");
            Directory.CreateDirectory(quarantine);
            Directory.Move(directory, Path.Combine(quarantine, Path.GetFileName(directory)));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var owner in roots.Values)
        {
            owner.Dispose();
        }
        roots.Clear();
    }
}
