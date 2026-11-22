using Yohuke.LiveRecorder.Infrastructure.Auth;
using Yohuke.LiveRecorder.Infrastructure.Bilibili;
using Yohuke.LiveRecorder.Server.Endpoints;
using Yohuke.LiveRecorder.Server.Realtime;

namespace Yohuke.LiveRecorder.Server.StartupFlows;

internal sealed class EndpointMappingStartupFlow(IReadOnlyCollection<string> developmentOrigins) : IApplicationStartupFlow
{
    void IApplicationStartupFlow.Execute(WebApplication app)
    {
        app.MapSetupEndpoints();
        app.MapUiSettingsEndpoints();
        app.MapIdentityEndpoints(developmentOrigins);
        app.MapBilibiliEndpoints();
        app.MapLibraryEndpoints();
        app.MapClipLibraryEndpoints();
        app.MapClipShareEndpoints();
        app.MapTrashEndpoints();
        app.MapPlaybackEventSnapshotEndpoints();
        app.MapStorageCacheEndpoints();
        app.MapAnalyticsExportEndpoints();
        app.MapSessionSegmentEndpoints();
        app.MapSessionConfigurationEndpoints();
        app.MapSessionBookmarkEndpoints();
        app.MapSessionLiveMarkerEndpoints();
        app.MapEventArtworkEndpoints();
        app.MapSessionTimelineEndpoints();
        app.MapManagementEndpoints();
        app.MapTranscodingEndpoints();
        app.MapClippingEndpoints();
        app.MapRecordingWorkerEndpoints();
        app.MapAiEndpoints();
        app.MapAiWorkerEndpoints();
        app.MapTaskHistoryEndpoints();
        app.MapServerLogEndpoints();
        app.MapHub<StatusHub>("/hubs/status", options => options.CloseOnAuthenticationExpiration = true).RequireAuthorization();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi().RequireAuthorization(policy => policy.RequireRole("Admin"));
        }
    }
}
