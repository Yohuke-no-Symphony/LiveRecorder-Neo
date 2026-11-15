using System.Globalization;
using System.Text.RegularExpressions;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Library;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Analytics;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Library;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Server.Endpoints;

/// <summary>直播分析工作簿下载与现有分析页面共享权限、选区和同传规则。</summary>
public static class AnalyticsExportEndpoints
{
    /// <summary>注册从完整事件观测生成多工作表 Excel 的只读接口。</summary>
    public static IEndpointRouteBuilder MapAnalyticsExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/sessions/{id}/analytics/export", async (string id, double? fromMs, double? toMs,
            HttpContext http, LibraryService library, IAppPaths paths, ISettingsStore settingsStore, IMongoContext context,
            AnalyticsExportService exporter, CancellationToken ct) =>
        {
            var detail = await library.GetAsync(id, ct);
            var pattern = TranslationDanmuPattern.Resolve(detail.Session, await settingsStore.GetAsync(ct));
            var streamer = await context.Collection<Streamer>("streamers").Find(item => item.Id == detail.Session.StreamerId).FirstOrDefaultAsync(ct);
            try
            {
                var file = await exporter.ExportAsync(detail, paths.RecordsDirectory, pattern, fromMs, toMs, CultureInfo.CurrentUICulture.Name, streamer?.Name, ct);
                http.Response.Headers.CacheControl = "private, no-store";
                return Results.File(file.Content, AnalyticsExportFile.CONTENT_TYPE, file.FileName);
            }
            catch (RegexMatchTimeoutException)
            {
                throw new InfrastructureException(MessageCode.TRANSLATION_DANMU_PATTERN_TIMEOUT);
            }
        }).RequireAuthorization(UserPermissionNames.VIEW_RECORDINGS, UserPermissionNames.VIEW_ANALYTICS);
        return endpoints;
    }
}
