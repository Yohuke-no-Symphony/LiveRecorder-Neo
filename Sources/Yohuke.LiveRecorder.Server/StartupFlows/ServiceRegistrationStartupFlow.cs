using System.Net;
using Microsoft.AspNetCore.Identity;
using Yohuke.DanmuCore.Services;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Library.LiveMarkers;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.AI;
using Yohuke.LiveRecorder.Infrastructure.Analytics;
using Yohuke.LiveRecorder.Infrastructure.Auth;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Library.LiveMarkers;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Persistence.Migrations;
using Yohuke.LiveRecorder.Infrastructure.Playback;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Infrastructure.Storage;
using Yohuke.LiveRecorder.Infrastructure.Tasks;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Media;
using Yohuke.LiveRecorder.Server.Bandwidth;
using Yohuke.LiveRecorder.Server.Configuration;
using Yohuke.LiveRecorder.Server.Endpoints;
using Yohuke.LiveRecorder.Server.Realtime;
using Yohuke.LiveRecorder.Server.Transcoding;

namespace Yohuke.LiveRecorder.Server.StartupFlows;

internal sealed class ServiceRegistrationStartupFlow : IBuilderStartupFlow
{
    void IBuilderStartupFlow.Execute(WebApplicationBuilder builder)
    {
        RegisterRecorderServices(builder);
        RegisterRuntimeServices(builder);
    }

    private static void RegisterRecorderServices(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<MediaBandwidthScheduler>();
        builder.Services.AddSingleton<IMediaBandwidthScheduler>(provider => provider.GetRequiredService<MediaBandwidthScheduler>());
        builder.Services.AddHostedService(provider => provider.GetRequiredService<MediaBandwidthScheduler>());
        builder.Services.AddSingleton<EventArchiveReader>();
        builder.Services.AddSingleton<LibraryService>();
        builder.Services.AddSingleton<ClipShareService>();
        builder.Services.AddSingleton<IPasswordHasher<Clip>, PasswordHasher<Clip>>();
        builder.Services.AddSingleton<ClipShareAccessCookie>();
        builder.Services.AddSingleton<SessionBookmarkService>();
        builder.Services.AddSingleton<LiveMarkerFileStore>();
        builder.Services.AddSingleton<LiveMarkerYamlParser>();
        builder.Services.AddSingleton<ISessionLiveMarkerService, SessionLiveMarkerService>();
        builder.Services.AddSingleton<SessionGroupingService>();
        builder.Services.AddSingleton<SessionConfigurationService>();
        builder.Services.AddSingleton<SessionEventReader>();
        builder.Services.AddSingleton<SessionEventExportService>();
        builder.Services.AddPlaybackEventSnapshotServices();
        builder.Services.AddSingleton<StorageService>();
        builder.Services.AddSingleton<StorageUsageService>();
        builder.Services.AddSingleton<StorageUsageMetadataService>();
        builder.Services.AddSingleton<CacheCleanupService>();
        builder.Services.AddSingleton<SessionTrashService>();
        builder.Services.AddSingleton<SessionSegmentTrashService>();
        builder.Services.AddOptions<ContinuousPlaybackOptions>()
            .Bind(builder.Configuration.GetSection("ContinuousPlayback"))
            .Validate(options => options.PlanCacheLimitBytes > 0
                && options.PlanCacheSlidingExpiration > TimeSpan.Zero
                && options.PlanCacheAbsoluteExpiration >= options.PlanCacheSlidingExpiration,
                "Continuous playback cache limits and expiration must be positive.")
            .ValidateOnStart();
        builder.Services.AddSingleton<PlaybackCache>();
        builder.Services.AddSingleton<ContinuousPlaybackService>();
        builder.Services.AddSingleton<PlaybackService>();
        builder.Services.AddSingleton<RenditionPlaybackService>();
        builder.Services.AddSingleton<MediaJobService>();
        builder.Services.AddSingleton<TaskHistoryService>();
        builder.Services.AddSingleton<TaskLogService>();
        builder.Services.AddSingleton<TaskEventLogStore>();
        builder.Services.AddSingleton<AnalyticsService>();
        builder.Services.AddSingleton<AnalyticsContributionService>();
        builder.Services.AddSingleton<AnalyticsExportService>();
        builder.Services.AddSingleton<ArtworkCache>();
        builder.Services.AddSingleton<ClipCoverService>();
        builder.Services.AddSingleton<SessionCoverService>();
        builder.Services.AddSingleton<IUserAvatarSource>(services => new BilibiliUserAvatarSource(services.GetRequiredService<IHttpClientFactory>().CreateClient("bilibili")));
        builder.Services.AddSingleton<AudienceAvatarService>();
        builder.Services.AddSingleton<EventArtworkService>();
        builder.Services.AddSingleton<SessionTimelineService>();
    }

    private static void RegisterRuntimeServices(WebApplicationBuilder builder)
    {
        builder.Services.AddMigrations();
        builder.Services.AddSingleton<MigrationStartupService>();
        builder.Services.AddHostedService(services => services.GetRequiredService<MigrationStartupService>());
        builder.Services.AddSingleton<StatusConnections>();
        builder.Services.AddSingleton<IUserSessionRevoker>(provider => provider.GetRequiredService<StatusConnections>());
        builder.Services.AddSingleton<IStatusPublisher, SignalRStatusPublisher>();
        builder.Services.AddHttpClient("artwork", client => client.Timeout = TimeSpan.FromSeconds(15)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        builder.Services.AddIdentityServices();
        builder.Services.AddLiveRecorderMedia();
        builder.Services.AddSingleton<ITranscodeMediaInspector, TranscodeMediaInspector>();
        builder.Services.AddLiveRecorderTranscoding();
        builder.Services.AddOptions<TranscodingControlOptions>()
            .Bind(builder.Configuration.GetSection("TranscodingControl"))
            .Validate(value => !value.Enabled || (value.Port is > 0 and <= 65535
                && IPAddress.TryParse(value.ListenAddress, out _) && (!value.UseTls || !string.IsNullOrWhiteSpace(value.CertificatePath))
                && value.MaximumConnections is > 0 and <= 4096), "Worker control listener configuration is invalid.")
            .ValidateOnStart();
        builder.Services.AddHostedService<WorkerControlServer>();
        builder.Services.AddLiveRecorderAiInfrastructure();
        builder.Services.AddHostedService<DatabaseIndexWorker>();
        builder.Services.AddHostedService<RecordingSupervisor>();
        builder.Services.AddHostedService<MediaJobWorker>();
        builder.Services.AddHostedService<StorageMaintenanceWorker>();
        builder.Services.AddHostedService<ArtworkMaintenanceWorker>();
        builder.Services.AddHostedService<LiveMarkerMaintenanceWorker>();
        builder.Services.AddSignalR();
        builder.Services.AddOpenApi();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new PermissionSetJsonConverter()));
    }
}
