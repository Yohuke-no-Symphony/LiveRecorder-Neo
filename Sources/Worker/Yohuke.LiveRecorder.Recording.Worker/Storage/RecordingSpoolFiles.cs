using Yohuke.LiveRecorder.Worker.Common.Storage;

namespace Yohuke.LiveRecorder.Recording.Worker.Storage;

internal static class RecordingSpoolFiles
{
    internal static T Read<T>(string path) => WorkerJsonFileStore.Read<T>(path);

    internal static void Write<T>(string path, T value) => WorkerJsonFileStore.Write(path, value);

    internal static string Resolve(string root, string relative)
    {
        if (Path.IsPathFullyQualified(relative))
        {
            throw new InvalidDataException("A recording spool artifact must use a relative path.");
        }
        var result = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return result.StartsWith(prefix, comparison) ? result : throw new InvalidDataException("A recording artifact escaped its spool directory.");
    }

    internal static string SafeId(string value) => value.Length is > 0 and <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
        ? value : throw new InvalidDataException("Invalid recording spool identity.");
}
