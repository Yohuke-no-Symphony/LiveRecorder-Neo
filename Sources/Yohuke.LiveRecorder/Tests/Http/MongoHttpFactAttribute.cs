using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>只有显式指定隔离 MongoDB 后才运行需要数据库的 HTTP 回归。</summary>
public sealed class MongoHttpFactAttribute : FactAttribute
{
    /// <summary>将缺少数据库配置明确记录为跳过。</summary>
    public MongoHttpFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LIVE_RECORDER_TEST_MONGO")))
        {
            Skip = "Set LIVE_RECORDER_TEST_MONGO to an isolated disposable MongoDB instance.";
        }
    }
}
