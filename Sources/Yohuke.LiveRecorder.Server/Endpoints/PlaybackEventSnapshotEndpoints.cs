using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Net.Http.Headers;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Library;

namespace Yohuke.LiveRecorder.Server.Endpoints;

/// <summary>以已授权的条件读取交付浏览器持久化所需的完整事件与追加快照。</summary>
public static class PlaybackEventSnapshotEndpoints
{
    private static JsonSerializerOptions SnapshotJsonOptions { get; } = new(JsonFiles.Options)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { OmitRawProtocol } }
    };

    /// <summary>注册事件快照及仅对 JSON 启用的标准 Brotli、Gzip 压缩提供器。</summary>
    public static IServiceCollection AddPlaybackEventSnapshotServices(this IServiceCollection services)
    {
        services.AddSingleton<SessionEventSnapshotService>();
        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.MimeTypes = ["application/json"];
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
        });
        return services;
    }

    /// <summary>仅在完整事件快照路径使用协商后的响应压缩，不影响其他接口与视频资源。</summary>
    public static IApplicationBuilder UsePlaybackEventSnapshotCompression(this IApplicationBuilder app)
    {
        return app.UseWhen(http => http.Request.Path.StartsWithSegments("/api/sessions") &&
                http.Request.Path.Value?.EndsWith("/events/snapshot", StringComparison.OrdinalIgnoreCase) == true,
            branch => branch.UseResponseCompression());
    }

    /// <summary>注册当前观看权限、场次状态和同传规则共同约束的事件快照接口。</summary>
    public static IEndpointRouteBuilder MapPlaybackEventSnapshotEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/sessions/{id}/events/snapshot", async (string id, string? cursor,
            HttpContext http, LibraryService library, IAppPaths paths, ISettingsStore settingsStore,
            SessionEventSnapshotService snapshots, CancellationToken ct) =>
        {
            var detail = await library.GetAsync(id, ct);
            var pattern = TranslationDanmuPattern.Resolve(detail.Session, await settingsStore.GetAsync(ct));
            var result = await snapshots.ReadAsync(detail, paths.RecordsDirectory, pattern, cursor,
                http.Request.Headers.IfNoneMatch.ToString(), ct);
            http.Response.Headers.CacheControl = "private, no-cache, must-revalidate";
            http.Response.Headers.ETag = result.EntityTag;
            http.Response.Headers.Append(HeaderNames.Vary, "Accept-Language");
            http.Response.Headers.XContentTypeOptions = "nosniff";
            if (result.NotModified)
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }
            var snapshot = result.Snapshot!;
            if (snapshot.TranslationError is { } translationError)
            {
                snapshot.TranslationError = LocalizedText.Format(translationError, translationError);
            }
            foreach (var item in snapshot.Items)
            {
                if (item.Kind == "Gap")
                {
                    item.Text = LocalizedText.Format(item.TextCode, item.Text, item.TextArguments);
                }
            }
            return Results.Json(snapshot, SnapshotJsonOptions);
        }).RequireAuthorization(UserPermissionNames.VIEW_RECORDINGS);
        return endpoints;
    }

    private static void OmitRawProtocol(JsonTypeInfo type)
    {
        if (type.Type != typeof(RecordedEventEnvelope))
        {
            return;
        }
        for (var index = type.Properties.Count - 1; index >= 0; index--)
        {
            if (type.Properties[index].Name.Equals("rawJson", StringComparison.OrdinalIgnoreCase))
            {
                type.Properties.RemoveAt(index);
            }
        }
    }
}
