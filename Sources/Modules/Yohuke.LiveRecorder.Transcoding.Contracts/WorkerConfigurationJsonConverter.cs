using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yohuke.LiveRecorder.Transcoding.Contracts;

internal sealed class WorkerConfigurationJsonConverter(string kind) : JsonConverter<WorkerConfiguration>
{
    /// <inheritdoc />
    public override WorkerConfiguration? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.ValueKind == JsonValueKind.Null ? null : WorkerConfigurationProjection.Read(document.RootElement, kind);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, WorkerConfiguration value, JsonSerializerOptions options)
    {
        var model = WorkerConfigurationProjection.UserConfiguration(value, kind);
        JsonSerializer.Serialize(writer, model, model.GetType(), options);
    }
}
