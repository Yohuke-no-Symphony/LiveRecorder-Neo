using System.Security.Cryptography;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Storage;

internal sealed class WorkerWorkspace(string workerId)
{
    private readonly Lock sync = new();
    private readonly Dictionary<string, int> protectedPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> inputReservations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> outputReservations = new(StringComparer.Ordinal);

    internal WorkerWorkspaceLease Acquire(WorkerConfiguration configuration, TranscodeAssignment assignment, Action<string>? log = null)
    {
        var root = Path.Combine(Path.GetFullPath(configuration.TempDirectory), "live-recorder-worker", SafeJob(workerId));
        var inputKey = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{assignment.AssetId}:{assignment.SourceVersion}:{assignment.SourceLength}:{assignment.SourceETag}")));
        var input = Path.Combine(root, "inputs", inputKey);
        var output = Path.Combine(root, "jobs", SafeJob(assignment.JobId) + "-" + assignment.Attempt);
        lock (sync)
        {
            Directory.CreateDirectory(root);
            var sourcePath = Path.Combine(input, "source.flv");
            if (outputReservations.ContainsKey(output))
            {
                throw new InvalidOperationException("The same job attempt cannot acquire its workspace more than once");
            }
            var roots = protectedPaths.Keys.Select(path => Path.GetDirectoryName(Path.GetDirectoryName(path)!)!)
                .Append(root).Distinct(StringComparer.Ordinal);
            var sizes = roots.SelectMany(ReadSizes).ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            var inputBytes = inputReservations.ContainsKey(input) ? 0 : RemainingInput(sizes, input, assignment.SourceLength);
            var expectedOutput = Math.Max(64L * 1024 * 1024, assignment.SourceLength + assignment.SourceLength / 5);
            var expected = checked(inputBytes + Math.Max(0, expectedOutput - Size(sizes, output)));
            var occupied = Size(sizes, root);
            var remaining = inputReservations.Select(item => (Path: item.Key, Bytes: RemainingInput(sizes, item.Key, item.Value)))
                .Concat(outputReservations.Select(item => (Path: item.Key, Bytes: Math.Max(0, item.Value - Size(sizes, item.Key))))).ToArray();
            var reserved = remaining.Where(item => IsWithin(item.Path, root)).Sum(item => item.Bytes);
            var drive = FindDrive(root);
            var diskReserved = remaining.Where(item => FindDrive(item.Path).Name == drive.Name).Sum(item => item.Bytes);
            var free = drive.AvailableFreeSpace;
            var candidates = new[] { "inputs", "jobs" }.SelectMany(kind => Directory.Exists(Path.Combine(root, kind))
                    ? Directory.EnumerateDirectories(Path.Combine(root, kind)) : [])
                .Where(path => !protectedPaths.ContainsKey(path) && path != input && path != output)
                .OrderBy(Directory.GetLastWriteTimeUtc).ToArray();
            foreach (var candidate in candidates)
            {
                if (occupied + reserved + expected <= configuration.MaxCacheBytes && free - diskReserved - expected >= configuration.MinimumFreeBytes)
                {
                    break;
                }
                var bytes = Size(sizes, candidate);
                log?.Invoke(FormattableString.Invariant($"Removing unused cached directory {Path.GetFileName(candidate)} ({bytes} bytes) to reserve workspace capacity."));
                Directory.Delete(candidate, true);
                occupied -= bytes;
                free = FreeBytes(root);
            }
            if (occupied + reserved + expected > configuration.MaxCacheBytes || free - diskReserved - expected < configuration.MinimumFreeBytes)
            {
                throw new IOException("Insufficient worker cache capacity or free disk space. Adjust the cache limit or temporary directory on the server");
            }
            Protect(input);
            Protect(output);
            inputReservations[input] = assignment.SourceLength;
            outputReservations[output] = expectedOutput;
            Directory.CreateDirectory(input);
            Directory.CreateDirectory(output);
            Directory.SetLastWriteTimeUtc(input, DateTime.UtcNow);
            return new(sourcePath, output, () =>
            {
                lock (sync)
                {
                    Unprotect(input);
                    Unprotect(output);
                    if (!protectedPaths.ContainsKey(input))
                    {
                        inputReservations.Remove(input);
                    }
                    outputReservations.Remove(output);
                    if (Directory.Exists(output))
                    {
                        Directory.Delete(output, true);
                    }
                }
            });
        }
    }

    internal static long FreeBytes(string root) => FindDrive(root).AvailableFreeSpace;

    private static DriveInfo FindDrive(string root)
    {
        var full = Path.GetFullPath(root);
        var drive = DriveInfo.GetDrives().Where(item => item.IsReady && IsWithin(full, item.RootDirectory.FullName))
            .OrderByDescending(item => item.RootDirectory.FullName.Length).FirstOrDefault();
        return drive ?? throw new IOException("Unable to read free space on the disk containing the temporary directory");
    }

    private void Protect(string path) => protectedPaths[path] = protectedPaths.GetValueOrDefault(path) + 1;

    private void Unprotect(string path)
    {
        if (protectedPaths.GetValueOrDefault(path) <= 1)
        {
            protectedPaths.Remove(path);
        }
        else
        {
            protectedPaths[path]--;
        }
    }

    private static Dictionary<string, long> ReadSizes(string root)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                result[path] = new FileInfo(path).Length;
            }
            catch (FileNotFoundException)
            {
            }
        }
        return result;
    }

    private static long Size(Dictionary<string, long> sizes, string path) => sizes.Where(item => IsWithin(item.Key, path)).Sum(item => item.Value);

    private static long RemainingInput(Dictionary<string, long> sizes, string path, long expected)
    {
        var source = Path.Combine(path, "source.flv");
        if (sizes.GetValueOrDefault(source) == expected)
        {
            return 0;
        }
        var partial = sizes.GetValueOrDefault(source + ".partial");
        return partial <= expected ? expected - partial : expected;
    }

    private static bool IsWithin(string path, string directory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        return path.Equals(directory, comparison) || path.StartsWith(prefix, comparison);
    }

    private static string SafeJob(string value) => value.Length is > 0 and <= 128 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
        ? value : throw new InvalidDataException("Invalid job identifier");
}
