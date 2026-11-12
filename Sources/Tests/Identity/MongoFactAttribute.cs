using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>显式指定可丢弃 MongoDB 实例后才执行身份集成测试。</summary>
public sealed class MongoFactAttribute : FactAttribute
{
    /// <summary>未配置隔离实例时清楚标记为跳过。</summary>
    public MongoFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LIVE_RECORDER_TEST_MONGO")))
        {
            Skip = "Set LIVE_RECORDER_TEST_MONGO to an isolated disposable MongoDB instance.";
        }
    }
}
