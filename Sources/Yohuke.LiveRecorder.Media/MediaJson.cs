using System.Text.Json;

namespace Yohuke.LiveRecorder.Media;

internal static class MediaJson
{
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
