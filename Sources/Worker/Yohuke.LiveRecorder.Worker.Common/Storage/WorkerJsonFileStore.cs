using System.Text.Json;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Worker.Common.Storage;

internal static class WorkerJsonFileStore
{
    internal static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), WorkerProtocol.JsonOptions)
        ?? throw new InvalidDataException("The worker state file contains no document.");

    internal static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
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
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
