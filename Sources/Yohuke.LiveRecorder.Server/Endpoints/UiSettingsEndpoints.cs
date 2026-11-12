using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Domain.Localization;
using Yohuke.LiveRecorder.Server.Contracts;

namespace Yohuke.LiveRecorder.Server.Endpoints;

/// <summary>公开非敏感的界面语言配置。</summary>
public static class UiSettingsEndpoints
{
    /// <summary>注册安装前后均可读取的默认语言端点。</summary>
    public static IEndpointRouteBuilder MapUiSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/ui-settings", async (HttpContext http, IInstallationService installation, ISettingsStore settings, CancellationToken ct) =>
        {
            var language = installation.IsInstalled ? (await settings.GetAsync(ct)).DefaultLanguage : SupportedLanguages.DEFAULT;
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new PublicUiSettings(SupportedLanguages.IsSupported(language) ? language : SupportedLanguages.DEFAULT));
        });
        return endpoints;
    }
}
