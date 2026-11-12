using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

/// <summary>可读 UTF-8 文件与同卷原子替换。</summary>
public static class JsonFiles
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>先写临时文件，再原子发布完整 JSON。</summary>
    public static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                await JsonSerializer.SerializeAsync(stream, value, new JsonSerializerOptions(Options) { WriteIndented = true }, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
