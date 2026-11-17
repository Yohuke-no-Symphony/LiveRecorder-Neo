using MongoDB.Driver;

namespace Yohuke.LiveRecorder.Infrastructure.Persistence;

/// <summary>访问当前实例配置的管理数据库。</summary>
public interface IMongoContext
{
    IMongoDatabase Database { get; }

    /// <summary>访问指定的管理集合。</summary>
    IMongoCollection<T> Collection<T>(string name);
}
