namespace Yohuke.LiveRecorder.Infrastructure.Storage;

internal sealed class CacheCleanupFileSystem(string cacheRoot)
{
    private static StringComparison PathComparison { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private string Root { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot));

    internal static DateTime Cutoff(int minimumUnusedMinutes)
    {
        var now = DateTime.UtcNow;
        var age = TimeSpan.FromMinutes(minimumUnusedMinutes);
        return age.Ticks >= now.Ticks ? DateTime.MinValue : now - age;
    }

    internal bool IsSafeDirectory(string directory, CacheCleanupCategoryResult result)
    {
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!string.Equals(current, Root, PathComparison) && !current.StartsWith(Root + Path.DirectorySeparatorChar, PathComparison))
        {
            result.SkippedEntries++;
            return false;
        }
        try
        {
            while (true)
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                {
                    result.SkippedEntries++;
                    return false;
                }
                if (string.Equals(current, Root, PathComparison))
                {
                    return true;
                }
                current = Path.GetDirectoryName(current)!;
            }
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            result.FailedEntries++;
            return false;
        }
    }

    internal IReadOnlyList<DirectoryInfo> ReadDirectories(string directory, CacheCleanupCategoryResult result)
    {
        return ReadEntries(directory, result).OfType<DirectoryInfo>().ToArray();
    }

    internal IReadOnlyList<FileInfo> ReadFiles(string directory, CacheCleanupCategoryResult result,
        CancellationToken cancellationToken, bool recursive = false)
    {
        var files = new List<FileInfo>();
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in ReadEntries(current, result))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is DirectoryInfo child)
                {
                    if (recursive)
                    {
                        pending.Push(child.FullName);
                    }
                    else
                    {
                        result.SkippedEntries++;
                    }
                }
                else if (entry is FileInfo file)
                {
                    try
                    {
                        _ = file.Length;
                        files.Add(file);
                    }
                    catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                    {
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        result.FailedEntries++;
                    }
                }
            }
        }
        return files;
    }

    internal void DeleteFile(FileInfo file, CacheCleanupCategoryResult result)
    {
        if (!IsSafeDirectory(file.DirectoryName!, result))
        {
            return;
        }
        try
        {
            file.Refresh();
            if (!file.Exists)
            {
                return;
            }
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                result.SkippedEntries++;
                return;
            }
            var bytes = file.Length;
            file.Delete();
            result.DeletedFiles++;
            result.DeletedBytes += bytes;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            result.FailedEntries++;
        }
    }

    internal void RemoveEmptyTree(string directory, CacheCleanupCategoryResult result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var child in ReadDirectories(directory, result))
        {
            RemoveEmptyTree(child.FullName, result, cancellationToken);
        }
        RemoveEmptyDirectory(directory, result);
    }

    internal void RemoveEmptyDirectory(string directory, CacheCleanupCategoryResult result)
    {
        if (!IsSafeDirectory(directory, result))
        {
            return;
        }
        try
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            result.FailedEntries++;
        }
    }

    private IReadOnlyList<FileSystemInfo> ReadEntries(string directory, CacheCleanupCategoryResult result)
    {
        if (!IsSafeDirectory(directory, result))
        {
            return [];
        }
        try
        {
            var entries = new List<FileSystemInfo>();
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                try
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        result.SkippedEntries++;
                    }
                    else
                    {
                        entries.Add(entry);
                    }
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                {
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    result.FailedEntries++;
                }
            }
            return entries;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            result.FailedEntries++;
            return [];
        }
    }
}
