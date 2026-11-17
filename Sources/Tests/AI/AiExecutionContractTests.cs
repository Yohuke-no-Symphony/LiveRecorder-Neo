using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.Domain;
using Xunit;

namespace Yohuke.LiveRecorder.AI.Tests;

/// <summary>验证节点模型执行契约与主站设置的兼容边界。</summary>
public sealed class AiExecutionContractTests
{
    /// <summary>模型参数默认值与主站一致，调度和密钥字段不混入执行设置。</summary>
    [Fact]
    public void ExecutionSettingsKeepTheServerModelDefaults()
    {
        var execution = new AiExecutionSettings();
        var server = new AiSettings();
        foreach (var property in typeof(AiExecutionSettings).GetProperties())
        {
            var original = typeof(AiSettings).GetProperty(property.Name);
            Assert.NotNull(original);
            Assert.Equal(original.GetValue(server), property.GetValue(execution));
        }
        Assert.Null(typeof(AiExecutionSettings).GetProperty("Enabled"));
        Assert.Null(typeof(AiExecutionSettings).GetProperty("MaxConcurrentJobs"));
        Assert.Null(typeof(AiExecutionSettings).GetProperty("ApiKey"));
    }
}
