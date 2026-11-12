namespace Yohuke.LiveRecorder.Infrastructure.Configuration;

/// <summary>连接数据库前即可读取的安装配置。</summary>
public sealed class BootstrapConfiguration
{
    public bool Installed { get; set; }
    public string MongoConnectionString { get; set; } = "mongodb://127.0.0.1:27017";
    public string DatabaseName { get; set; } = "live_recorder";
    public string StoragePath { get; set; } = "";
}
