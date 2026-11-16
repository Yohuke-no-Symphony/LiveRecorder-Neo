using Microsoft.Extensions.DependencyInjection;

namespace Yohuke.LiveRecorder.Infrastructure.Recording;

/// <summary>Registers persistent recording ownership, dispatch and coherent streaming ingestion.</summary>
public static class RecordingServiceCollectionExtensions
{
    /// <summary>Adds recording node coordination against existing account, storage and media library services.</summary>
    public static IServiceCollection AddLiveRecorderRecording(this IServiceCollection services)
    {
        services.AddSingleton<RecordingOwnershipService>();
        services.AddSingleton<RecordingAccountProvider>();
        services.AddSingleton<RecordingJobScheduler>();
        services.AddSingleton<RecordingIngestService>();
        services.AddSingleton<RecordingHandoffService>();
        services.AddSingleton<RecordingTargetStatusService>();
        return services;
    }
}
