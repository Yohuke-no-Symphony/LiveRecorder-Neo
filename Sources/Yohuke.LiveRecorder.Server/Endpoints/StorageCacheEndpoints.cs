using Yohuke.LiveRecorder.Infrastructure.Storage;
using Yohuke.LiveRecorder.Server.Contracts;

namespace Yohuke.LiveRecorder.Server.Endpoints;

/// <summary>向管理员提供可重建缓存清理，沿用宿主的认证及 CSRF 验证。</summary>
public static class StorageCacheEndpoints
{
    /// <summary>注册只接受固定类别的缓存清理接口。</summary>
    public static IEndpointRouteBuilder MapStorageCacheEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var admin = endpoints.MapGroup("/api/storage/cache").RequireAuthorization(policy => policy.RequireRole("Admin"));
        admin.MapPost("/cleanup", async (CacheCleanupRequest request, CacheCleanupService cleanup, CancellationToken ct) =>
            Results.Ok(await cleanup.CleanupAsync(request.Categories, request.MinimumUnusedMinutes, ct)));
        return endpoints;
    }
}
