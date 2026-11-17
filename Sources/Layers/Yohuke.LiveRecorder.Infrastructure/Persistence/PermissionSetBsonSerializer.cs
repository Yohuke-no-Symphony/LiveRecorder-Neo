using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Auth;

namespace Yohuke.LiveRecorder.Infrastructure.Persistence;

/// <summary>兼容旧权限文档并统一写入权限标识数组。</summary>
public sealed class PermissionSetBsonSerializer : SerializerBase<PermissionSet>
{
    /// <inheritdoc />
    public override PermissionSet Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        var enabled = new List<string>();
        switch (reader.GetCurrentBsonType())
        {
            case BsonType.Array:
                reader.ReadStartArray();
                while (reader.ReadBsonType() != BsonType.EndOfDocument)
                {
                    if (reader.GetCurrentBsonType() != BsonType.String)
                    {
                        throw new FormatException("权限必须是标识数组。");
                    }

                    enabled.Add(reader.ReadString());
                }

                reader.ReadEndArray();
                return new(enabled);
            case BsonType.Document:
                reader.ReadStartDocument();
                while (reader.ReadBsonType() != BsonType.EndOfDocument)
                {
                    var name = reader.ReadName();
                    if (LegacyPermissionMapping.GetId(name) is not { } id)
                    {
                        reader.SkipValue();
                        continue;
                    }

                    if (reader.GetCurrentBsonType() != BsonType.Boolean)
                    {
                        throw new FormatException("旧权限字段必须为布尔值。");
                    }

                    if (reader.ReadBoolean())
                    {
                        enabled.Add(id);
                    }
                }

                reader.ReadEndDocument();
                return LegacyPermissionMapping.Convert(enabled);
            default:
                throw new FormatException("权限数据格式无效。");
        }
    }

    /// <inheritdoc />
    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, PermissionSet value)
    {
        context.Writer.WriteStartArray();
        foreach (var permission in value)
        {
            context.Writer.WriteString(permission);
        }

        context.Writer.WriteEndArray();
    }
}
