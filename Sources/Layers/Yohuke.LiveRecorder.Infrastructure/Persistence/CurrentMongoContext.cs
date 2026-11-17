using MongoDB.Driver;
using Yohuke.LiveRecorder.Infrastructure.Configuration;

namespace Yohuke.LiveRecorder.Infrastructure.Persistence;

/// <summary>将管理数据库访问转发到同一已发布实例的本地客户端。</summary>
public sealed class CurrentMongoContext(CurrentInstanceStorage storage) : IMongoContext
{
    public IMongoDatabase Database => storage.Current.Mongo.Database;

    /// <inheritdoc />
    public IMongoCollection<T> Collection<T>(string name)
    {
        return storage.Current.Mongo.Collection<T>(name);
    }
}
