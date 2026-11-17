using Microsoft.Extensions.DependencyInjection;
using Yohuke.LiveRecorder.Infrastructure.Jobs;

namespace Yohuke.LiveRecorder.Infrastructure.Clipping;

/// <summary>Registers remote clipping against the existing persistent media job queue.</summary>
public static class ClippingServiceCollectionExtensions
{
    /// <summary>Adds input planning, leases, upload verification and shared clip publication.</summary>
    public static IServiceCollection AddLiveRecorderClipping(this IServiceCollection services)
    {
        services.AddSingleton<ClipInputPlanner>();
        services.AddSingleton<ClipLeaseService>();
        services.AddSingleton<ClipPlanPreparation>();
        services.AddHostedService(provider => provider.GetRequiredService<ClipPlanPreparation>());
        services.AddSingleton<ClippingJobScheduler>();
        services.AddSingleton<ClipPublicationService>();
        services.AddSingleton<ClipTransferService>();
        return services;
    }
}
