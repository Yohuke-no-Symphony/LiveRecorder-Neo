using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Yohuke.DanmuCore.Services;
using Yohuke.LiveRecorder.Infrastructure.Files;
using Yohuke.LiveRecorder.Infrastructure.Playback;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证真实宿主的播放扩展组合及时间轴与管理员财务分析的权限边界。</summary>
public sealed class PlaybackIntegrationTests
{
    /// <summary>新增接口服务可由真实宿主解析，普通成员可读时间轴且不能读取管理员财务统计。</summary>
    [Fact]
    public async Task HostComposesArtworkAndTimelineAndPreservesDistinctAccessPolicies()
    {
        await using var fixture = await HttpServerFixture.CreateAsync();
        var services = fixture.App.Services;
        Assert.IsType<SessionTimelineService>(services.GetRequiredService<SessionTimelineService>());
        Assert.IsType<EventArtworkService>(services.GetRequiredService<EventArtworkService>());
        Assert.IsType<AudienceAvatarService>(services.GetRequiredService<AudienceAvatarService>());
        Assert.IsType<BilibiliUserAvatarSource>(services.GetRequiredService<IUserAvatarSource>());
        var routes = ((IEndpointRouteBuilder)fixture.App).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
        var timeline = routes.Single(route => route.RoutePattern.RawText == "/api/sessions/{id}/timeline");
        var analytics = routes.Single(route => route.RoutePattern.RawText == "/api/sessions/{id}/analytics");
        var viewer = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "viewer"), new Claim(ClaimTypes.Role, "User")], "Integration"));
        var administrator = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "admin"), new Claim(ClaimTypes.Role, "Admin")], "Integration"));
        var authorization = services.GetRequiredService<IAuthorizationService>();
        var timelinePolicy = await PolicyAsync(fixture.App, timeline);
        var analyticsPolicy = await PolicyAsync(fixture.App, analytics);

        Assert.True((await authorization.AuthorizeAsync(viewer, null, timelinePolicy)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), null, timelinePolicy)).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(viewer, null, analyticsPolicy)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(administrator, null, analyticsPolicy)).Succeeded);
    }

    private static async Task<AuthorizationPolicy> PolicyAsync(WebApplication app, Endpoint endpoint)
    {
        var provider = app.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var inherited = await AuthorizationPolicy.CombineAsync(provider, endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.NotNull(inherited);
        return AuthorizationPolicy.Combine(new[] { inherited }.Concat(endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>()));
    }
}
