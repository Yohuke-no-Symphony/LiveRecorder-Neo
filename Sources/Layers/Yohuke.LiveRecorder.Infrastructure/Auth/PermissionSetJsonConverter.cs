using System.Text.Json;
using System.Text.Json.Serialization;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Auth;

/// <summary>以数组传输权限，仅文件读取允许旧布尔结构。</summary>
public sealed class PermissionSetJsonConverter(bool allowLegacy = false) : JsonConverter<PermissionSet>
{
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override PermissionSet Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var enabled = new List<string>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                {
                    throw new JsonException("权限必须是非空标识数组。");
                }

                enabled.Add(item.GetString()!);
            }

            return new(enabled);
        }

        if (allowLegacy && root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (LegacyPermissionMapping.GetId(property.Name) is not { } id)
                {
                    continue;
                }

                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new JsonException("旧权限字段必须为布尔值。");
                }

                if (property.Value.GetBoolean())
                {
                    enabled.Add(id);
                }
            }

            return LegacyPermissionMapping.Convert(enabled);
        }

        throw new JsonException("权限必须是标识数组。");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, PermissionSet value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var permission in value)
        {
            writer.WriteStringValue(permission);
        }

        writer.WriteEndArray();
    }
}
