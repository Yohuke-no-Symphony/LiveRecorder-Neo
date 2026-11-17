using Yohuke.LiveRecorder.Application.Localization;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Infrastructure.Configuration;

namespace Yohuke.LiveRecorder.Infrastructure.Persistence;

/// <summary>按安装配置复用 MongoDB 客户端。</summary>
public sealed class MongoContext : IMongoContext
{
    private readonly IBootstrapStore bootstrap;
    private readonly object gate = new();
    private MongoClient? client;
    private string? connection;

    /// <summary>在任何类型化数据库访问前初始化兼容序列化器。</summary>
    public MongoContext(IBootstrapStore bootstrap)
    {
        MongoSerialization.EnsureInitialized();
        this.bootstrap = bootstrap;
    }

    public IMongoDatabase Database
    {
        get
        {
            var configuration = bootstrap.Current ?? throw new InfrastructureException(MessageCode.SETUP_REQUIRED, 503);
            lock (gate)
            {
                if (client is null || connection != configuration.MongoConnectionString)
                {
                    var options = MongoClientSettings.FromConnectionString(configuration.MongoConnectionString);
                    options.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
                    options.ConnectTimeout = TimeSpan.FromSeconds(5);
                    client = new MongoClient(options);
                    connection = configuration.MongoConnectionString;
                }
                return client.GetDatabase(configuration.DatabaseName);
            }
        }
    }

    /// <summary>访问指定管理集合；弹幕不得存入集合。</summary>
    public IMongoCollection<T> Collection<T>(string name) => Database.GetCollection<T>(name);
}
