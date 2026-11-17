using Microsoft.Extensions.DependencyInjection;
using Yohuke.LiveRecorder.Application.AI;

namespace Yohuke.LiveRecorder.Infrastructure.AI;

/// <summary>注册归档适配、持久作业服务和独立后台执行器。</summary>
public static class AiInfrastructureServiceCollectionExtensions
{
    /// <summary>添加服务端适配，不向 AI 内核引入基础设施依赖。</summary>
    public static IServiceCollection AddLiveRecorderAiInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<AiArchiveDataSource>();
        services.AddSingleton<AiInputManifestFactory>();
        services.AddSingleton<AiAnalysisService>();
        services.AddSingleton<AiLiveMarkerPublisher>();
        services.AddSingleton<AiConnectionTestCoordinator>();
        services.AddSingleton<AiJobExecutionService>();
        services.AddSingleton<AiJobScheduler>();
        services.AddSingleton<IAiConnectionTester, AiRemoteConnectionTester>();
        services.AddHostedService<AiJobMaintenanceWorker>();
        return services;
    }
}
