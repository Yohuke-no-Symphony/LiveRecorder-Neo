using System.Text.Json;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Recording.Worker.Storage;

internal static class RecordingSpoolFiles
{
    internal static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), WorkerProtocol.JsonOptions)
        ?? throw new InvalidDataException("The recording spool contains an empty manifest.");

    internal static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using (var output = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(output, value, WorkerProtocol.JsonOptions);
                output.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

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
