using System.Text.Json;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Domain.Localization;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Server.Configuration;
using Yohuke.LiveRecorder.Server.Contracts;

namespace Yohuke.LiveRecorder.Server.Endpoints;

/// <summary>配置管理与敏感字段写入保护。</summary>
public static class ManagementEndpoints
{
    /// <summary>注册仅管理员可访问的设置端点。</summary>
    public static IEndpointRouteBuilder MapManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var admin = endpoints.MapGroup("/api/settings").RequireAuthorization(policy => policy.RequireRole("Admin"));
        admin.MapGet("", async (ISettingsStore settingsStore, CancellationToken ct) => Results.Ok(Redact(await settingsStore.GetAsync(ct))));
        admin.MapGet("/paths", (IAppPaths paths, StartupConfigurationStore startupConfiguration) => Results.Ok(GetPaths(paths, startupConfiguration)));
        admin.MapPut("/paths", async (SettingsPathsRequest request, IAppPaths paths, StartupConfigurationStore startupConfiguration, WebUiLocation webUi, CancellationToken ct) =>
        {
            startupConfiguration.ValidatePaths(request.DataDirectory, request.RecordsDirectory);
            webUi.EnsurePrivate(startupConfiguration.ResolveDataDirectory(request.DataDirectory));
            webUi.EnsurePrivate(startupConfiguration.ResolveRecordsDirectory(request.RecordsDirectory));
            await startupConfiguration.SaveAsync(request.DataDirectory, request.RecordsDirectory, ct);
            return Results.Ok(GetPaths(paths, startupConfiguration));
        });
        admin.MapPut("", async (ServerSettings request, ISettingsStore settingsStore, ISecretProtector protector, CancellationToken ct) =>
        {
            Validate(request);
            var previous = await settingsStore.GetAsync(ct);
            request.Smtp.Password = string.IsNullOrEmpty(request.Smtp.Password) ? previous.Smtp.Password : protector.Protect(request.Smtp.Password);
            foreach (var provider in request.OAuthProviders)
            {
                var original = previous.OAuthProviders.FirstOrDefault(x => x.Id == provider.Id);
                provider.ClientSecret = string.IsNullOrEmpty(provider.ClientSecret) ? original?.ClientSecret ?? "" : protector.Protect(provider.ClientSecret);
            }
            await settingsStore.SaveAsync(request, ct);
            return Results.Ok(Redact(request));
        });
        return endpoints;
    }

    private static SettingsPathsResponse GetPaths(IAppPaths paths, StartupConfigurationStore startupConfiguration)
    {
        var configured = startupConfiguration.GetSnapshot();
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var records = configured.RecordsDirectory!;
        var restartRequired = !string.Equals(Path.TrimEndingDirectorySeparator(paths.DataDirectory), Path.TrimEndingDirectorySeparator(startupConfiguration.ResolveDataDirectory(configured.DataDirectory)), comparison)
                              || !string.Equals(Path.TrimEndingDirectorySeparator(paths.RecordsDirectory), Path.TrimEndingDirectorySeparator(startupConfiguration.ResolveRecordsDirectory(records)), comparison);
        return new(paths.DataDirectory, paths.RecordsDirectory, paths.CacheDirectory, startupConfiguration.ConfigFile, configured.DataDirectory, records, restartRequired);
    }

    private static ServerSettings Redact(ServerSettings value)
    {
        var clone = JsonSerializer.Deserialize<ServerSettings>(JsonSerializer.Serialize(value, JsonFiles.Options), JsonFiles.Options)!;
        clone.Smtp.Password = "";
        foreach (var provider in clone.OAuthProviders)
        {
            provider.ClientSecret = "";
        }
        return clone;
    }

    private static void Validate(ServerSettings settings)
    {
        if (settings.StreamDisconnectTimeoutMinutes is < 1 or > 1440)
        {
            throw new InfrastructureException(MessageCode.SETTINGS_INVALID);
        }
        if (!SupportedLanguages.IsSupported(settings.DefaultLanguage))
        {
            throw new InfrastructureException(MessageCode.LANGUAGE_UNSUPPORTED);
        }
        if (settings.Quality <= 0 || settings.SegmentBytes < 0 || settings.ClipRetentionDays is < 1 or > 3650 || settings.TrashRetentionDays is < 1 or > 3650 || settings.MediaConcurrency is < 1 or > 16 || settings.PlaybackConcurrency is < 1 or > 32 || settings.MonitorIntervalSeconds is < 5 or > 600 || settings.SessionGraceMinutes is < 0 or > 120 || settings.MinimumFreeBytes < 0 || settings.CacheLimitBytes < 64 * 1024 * 1024 || settings.CacheRetentionMinutes < 1 || string.IsNullOrWhiteSpace(settings.FfmpegPath) || string.IsNullOrWhiteSpace(settings.FfprobePath))
        {
            throw new InfrastructureException(MessageCode.SETTINGS_INVALID);
        }
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            throw new InfrastructureException(MessageCode.SETTINGS_TIME_ZONE_INVALID);
        }
        if (settings.OAuthProviders.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != settings.OAuthProviders.Count)
        {
            throw new InfrastructureException(MessageCode.SETTINGS_OAUTH_DUPLICATE_ID);
        }
        foreach (var provider in settings.OAuthProviders.Where(x => x.Enabled))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(provider.Id, "^[a-zA-Z0-9_-]{1,50}$") || new[] { provider.AuthorizationEndpoint, provider.TokenEndpoint, provider.UserInfoEndpoint }.Any(x => !Uri.TryCreate(x, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))))
            {
                throw new InfrastructureException(MessageCode.SETTINGS_OAUTH_ENDPOINT_INVALID);
            }
        }
    }
}
