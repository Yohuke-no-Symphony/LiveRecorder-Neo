using System.Text.Json;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Server.Configuration;

namespace Yohuke.LiveRecorder.Server.Endpoints;

/// <summary>配置管理与敏感字段写入保护。</summary>
public static class ManagementEndpoints
{
    /// <summary>注册仅管理员可访问的设置端点。</summary>
    public static IEndpointRouteBuilder MapManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var admin = endpoints.MapGroup("/api/settings").RequireAuthorization(policy => policy.RequireRole("Admin"));
        admin.MapGet("", async (ISettingsStore settingsStore, CancellationToken ct) => Results.Ok(Redact(await settingsStore.GetAsync(ct))));
        admin.MapPut("", async (ServerSettings request, ISettingsStore settingsStore, ISecretProtector protector, MongoContext context, WebUiLocation webUi, CancellationToken ct) =>
        {
            Validate(request);
            webUi.EnsurePrivate(request.StoragePath);
            var previous = await settingsStore.GetAsync(ct);
            if (Path.GetFullPath(request.StoragePath) != Path.GetFullPath(previous.StoragePath) && await context.Collection<BroadcastSession>("sessions").Find(FilterDefinition<BroadcastSession>.Empty).AnyAsync(ct))
            {
                throw new InfrastructureException("已有录播时不能直接修改存储根目录。", 409);
            }
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
        if (string.IsNullOrWhiteSpace(settings.StoragePath) || !Path.IsPathRooted(settings.StoragePath) || settings.Quality <= 0 || settings.SegmentBytes < 0 || settings.ClipRetentionDays is < 1 or > 3650 || settings.TrashRetentionDays is < 1 or > 3650 || settings.MediaConcurrency is < 1 or > 16 || settings.PlaybackConcurrency is < 1 or > 32 || settings.MonitorIntervalSeconds is < 5 or > 600 || settings.SessionGraceMinutes is < 0 or > 120 || settings.MinimumFreeBytes < 0 || settings.CacheLimitBytes < 64 * 1024 * 1024 || settings.CacheRetentionMinutes < 1 || string.IsNullOrWhiteSpace(settings.FfmpegPath) || string.IsNullOrWhiteSpace(settings.FfprobePath))
        {
            throw new InfrastructureException("设置参数无效。");
        }
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            throw new InfrastructureException("时区无效。");
        }
        if (settings.OAuthProviders.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != settings.OAuthProviders.Count)
        {
            throw new InfrastructureException("OAuth 提供方标识重复。");
        }
        foreach (var provider in settings.OAuthProviders.Where(x => x.Enabled))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(provider.Id, "^[a-zA-Z0-9_-]{1,50}$") || new[] { provider.AuthorizationEndpoint, provider.TokenEndpoint, provider.UserInfoEndpoint }.Any(x => !Uri.TryCreate(x, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))))
            {
                throw new InfrastructureException("OAuth 端点或标识无效。");
            }
        }
    }
}
