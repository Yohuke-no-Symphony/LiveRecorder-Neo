using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Server.Endpoints;
using Yohuke.LiveRecorder.Server.Localization;
using Yohuke.LiveRecorder.Server.Transcoding;

namespace Yohuke.LiveRecorder.Server.StartupFlows;

internal sealed class RequestPipelineStartupFlow(CurrentInstanceStorage currentStorage, string[] developmentOrigins) : IApplicationStartupFlow
{
    void IApplicationStartupFlow.Execute(WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseRouting();
        if (developmentOrigins.Length > 0)
        {
            app.UseCors();
        }
        app.Use(async (http, next) =>
        {
            var path = http.Request.Path;
            if (!currentStorage.IsReady && (path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs"))
                && path != "/api/setup" && path != "/api/setup/status" && path != "/api/auth/me"
                && (currentStorage.IsInstalled || path != "/api/ui-settings"))
            {
                var code = currentStorage.IsInstalled ? MessageCode.REQUEST_DATABASE_UNAVAILABLE : MessageCode.SETUP_REQUIRED;
                await LocalizedProblem.Create(code, 503).ExecuteAsync(http);
                return;
            }
            await next();
        });
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.Use(async (http, next) =>
        {
            var workerEndpoint = http.GetEndpoint()?.Metadata.GetMetadata<WorkerApiEndpointMetadata>() is not null;
            if (workerEndpoint)
            {
                var limit = HttpMethods.IsPut(http.Request.Method) ? 8L * 1024 * 1024 : 16L * 1024 * 1024;
                var bodySize = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (bodySize is { IsReadOnly: false })
                {
                    bodySize.MaxRequestBodySize = limit;
                }
                if (http.Request.ContentLength > limit)
                {
                    http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return;
                }
            }
            if (!workerEndpoint && http.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) && !HttpMethods.IsOptions(http.Request.Method))
            {
                await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
            }
            await next();
        });
        app.UsePlaybackEventSnapshotCompression();
    }
}
