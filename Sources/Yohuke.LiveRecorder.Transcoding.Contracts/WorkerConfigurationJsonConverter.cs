using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yohuke.LiveRecorder.Transcoding.Contracts;

internal sealed class WorkerConfigurationJsonConverter(string kind) : JsonConverter<WorkerConfiguration>
{
    private static JsonSerializerOptions LegacyOptions { get; } = new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public override WorkerConfiguration? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = JsonSerializer.Deserialize<WorkerConfiguration>(ref reader, LegacyOptions);
        return value is null ? null : WorkerConfigurationProjection.Normalize(value, kind);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, WorkerConfiguration value, JsonSerializerOptions options)
    {
        var model = WorkerConfigurationProjection.UserConfiguration(value, kind);
        JsonSerializer.Serialize(writer, model, model.GetType(), options);
    }
}
