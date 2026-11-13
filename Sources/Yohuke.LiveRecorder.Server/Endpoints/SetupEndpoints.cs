using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Server.Contracts;
using Yohuke.LiveRecorder.Server.Configuration;

namespace Yohuke.LiveRecorder.Server.Endpoints;

/// <summary>映射新实例安装和已有实例恢复，安装完成后封闭入口。</summary>
public static class SetupEndpoints
{
    /// <summary>注册安装状态与受令牌保护的安装端点。</summary>
    public static IEndpointRouteBuilder MapSetupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/setup/status", (IInstallationService installation, IAppPaths paths, StartupConfigurationStore configuration) => installation.IsInstalled
            ? Results.Ok((object)new { installed = true, requiresToken = false })
            : Results.Ok((object)new { installed = false, requiresToken = true, dataDirectory = paths.DataDirectory, recordsDirectory = paths.RecordsDirectory, configFile = configuration.ConfigFile }));
        endpoints.MapPost("/api/setup", async (SetupRequest request, IInstallationService installation, StartupConfigurationStore configuration, WebUiLocation webUi, IOptionsMonitor<CookieAuthenticationOptions> authenticationOptions, IOptions<AntiforgeryOptions> antiforgeryOptions, HttpContext http, CancellationToken cancellationToken) =>
        {
            if (installation.IsInstalled)
            {
                throw new InfrastructureException(MessageCode.SETUP_COMPLETED, 409);
            }
            configuration.ValidatePaths(request.DataDirectory, request.RecordsDirectory);
            webUi.EnsurePrivate(configuration.ResolveDataDirectory(request.DataDirectory));
            webUi.EnsurePrivate(configuration.ResolveRecordsDirectory(request.RecordsDirectory));
            await installation.InstallAsync(new InstallRequest(request.Token, request.Email, request.Password, request.UserName, request.MongoConnectionString, request.DatabaseName, request.DataDirectory, request.RecordsDirectory, request.Mode, request.FfmpegPath, request.FfprobePath), cancellationToken);
            var authentication = authenticationOptions.Get(CookieAuthenticationDefaults.AuthenticationScheme);
            authentication.CookieManager.DeleteCookie(http, authentication.Cookie.Name!, authentication.Cookie.Build(http));
            var antiforgery = antiforgeryOptions.Value.Cookie;
            http.Response.Cookies.Delete(antiforgery.Name!, antiforgery.Build(http));
            if (antiforgery.Name != "lr.csrf")
            {
                http.Response.Cookies.Delete("lr.csrf", antiforgery.Build(http));
            }
            return Results.Ok(new { installed = true });
        });
        return endpoints;
    }
}
