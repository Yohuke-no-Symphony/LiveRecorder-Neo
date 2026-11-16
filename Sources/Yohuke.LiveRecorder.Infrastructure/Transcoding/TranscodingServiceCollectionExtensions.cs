using Microsoft.Extensions.DependencyInjection;
using Yohuke.LiveRecorder.Infrastructure.Clipping;
using Yohuke.LiveRecorder.Infrastructure.Recording;

namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

/// <summary>注册独立转码队列、传输、成品目录及恢复服务。</summary>
public static class TranscodingServiceCollectionExtensions
{
    /// <summary>复用主站存储、库权限和媒体探测边界，注册转码基础设施。</summary>
    public static IServiceCollection AddLiveRecorderTranscoding(this IServiceCollection services)
    {
        services.AddLiveRecorderClipping();
        services.AddLiveRecorderRecording();
        services.AddSingleton<TranscodeAssetRegistry>();
        services.AddSingleton<TranscodeWorkerService>();
        services.AddSingleton<TranscodeLeaseService>();
        services.AddSingleton<TranscodeJobScheduler>();
        services.AddSingleton<TranscodeTransferService>();
        services.AddSingleton<TranscodeRenditionCatalog>();
        services.AddSingleton<TranscodeWorkerLogStore>();
        services.AddSingleton<TranscodeLifecycle>();
        services.AddSingleton<TranscodingCoordinator>();
        services.AddHostedService<TranscodeMaintenanceService>();
        return services;
    }
}
