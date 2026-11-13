using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Persistence;

/// <summary>只读导入旧实例存于 MongoDB 的全局设置。</summary>
public static class LegacySettingsReader
{
    /// <summary>读取旧设置，舍弃由新实例启动目录取代的绝对录像路径。</summary>
    public static async Task<ServerSettings?> ReadAsync(IMongoDatabase database, CancellationToken cancellationToken = default)
    {
        var document = await database.GetCollection<BsonDocument>("settings").Find(new BsonDocument("_id", "server")).FirstOrDefaultAsync(cancellationToken);
        if (document is null)
        {
            return null;
        }
        document.Remove("StoragePath");
        return BsonSerializer.Deserialize<ServerSettings>(document);
    }
}
