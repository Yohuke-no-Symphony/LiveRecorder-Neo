using MongoDB.Bson.Serialization;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Persistence;

/// <summary>在读取管理集合前一次性安装兼容序列化规则。</summary>
public static class MongoSerialization
{
    static MongoSerialization()
    {
        BsonSerializer.RegisterSerializer(new PermissionSetBsonSerializer());
        BsonClassMap.RegisterClassMap<UserAccount>(mapping =>
        {
            mapping.AutoMap();
            mapping.SetIgnoreExtraElements(true);
            mapping.MapMember(user => user.AvatarRevision).SetIgnoreIfNull(true);
            mapping.MapMember(user => user.PendingEmailChange).SetIgnoreIfNull(true);
        });
        BsonClassMap.RegisterClassMap<Clip>(mapping =>
        {
            mapping.AutoMap();
            mapping.MapMember(clip => clip.ShareToken).SetIgnoreIfNull(true);
            mapping.MapMember(clip => clip.ShareVisibility).SetIgnoreIfNull(true);
            mapping.MapMember(clip => clip.SharePasswordHash).SetIgnoreIfNull(true);
        });
    }

    /// <summary>触发线程安全的进程级序列化初始化。</summary>
    public static void EnsureInitialized()
    {
    }
}
