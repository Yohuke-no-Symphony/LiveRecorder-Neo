using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yohuke.LiveRecorder.Application.AI;

namespace Yohuke.LiveRecorder.AI;

/// <summary>注册独立的模型客户端和分析编排。</summary>
public static class AiServiceCollectionExtensions
{
    /// <summary>使用无自动重定向的客户端，避免把服务密钥发送至跳转地址。</summary>
    public static IServiceCollection AddLiveRecorderAi(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient<IAiChatClient, OpenAiCompatibleChatClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddTransient<IAiAnalysisRunner, AiAnalysisRunner>();
        services.AddTransient<IAiConnectionTester, AiConnectionTester>();
        return services;
    }
}
