using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Yohuke.DanmuCore.Models.Accounts;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Bilibili;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>验证搜索使用后台解析的采集账号，并保持查询和录制添加分离。</summary>
public sealed class BilibiliRoomSearchIntegrationTests
{
    [MongoFact]
    public async Task ExplicitAccountIsUsedForSearchWithoutPersistingRecordingTargets()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        await AddAccountAsync(fixture, 1, "other-session");
        var selected = await AddAccountAsync(fixture, 2, "selected-session");
        fixture.Http.Handle = request =>
        {
            var cookie = request.Headers.GetValues("Cookie").Single();
            Assert.Contains("SESSDATA=selected-session", cookie);
            Assert.DoesNotContain("other-session", cookie);
            Assert.Contains("page=2", request.RequestUri!.Query);
            return Task.FromResult(Json("""{"code":0,"data":{"numPages":3,"result":[{"uid":42,"roomid":21573665,"uname":"主播","is_live":true}]}}"""));
        };
        using var service = CreateService(fixture);

        var result = await service.SearchRoomsAsync("主播", 2, selected.Id);

        Assert.Equal("21573665", Assert.Single(result.Results).RoomId);
        Assert.True(result.HasMore);
        Assert.Empty(await fixture.Context.Collection<Streamer>("streamers").Find(FilterDefinition<Streamer>.Empty).ToListAsync());
        Assert.DoesNotContain("selected-session", JsonSerializer.Serialize(result));
    }

    [MongoFact]
    public async Task FollowingSearchResolvesDefaultValidAccount()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        await AddAccountAsync(fixture, 1, "expired-session", "Expired");
        await AddAccountAsync(fixture, 2, "valid-session");
        fixture.Http.Handle = request =>
        {
            Assert.Equal("/x/polymer/web-dynamic/v1/portal", request.RequestUri!.AbsolutePath);
            Assert.Contains("SESSDATA=valid-session", request.Headers.GetValues("Cookie").Single());
            return Task.FromResult(Json("""{"code":0,"data":{"live_users":{"items":[{"mid":42,"room_id":21573665,"uname":"主播"}]}}}"""));
        };
        using var service = CreateService(fixture);

        var result = await service.SearchRoomsAsync("");

        Assert.Equal(["following"], Assert.Single(result.Results).Sources);
        Assert.True(result.Results[0].IsLive);
    }

    [MongoFact]
    public async Task ExpiredSelectedAccountCannotFallBackToAnotherIdentity()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        var expired = await AddAccountAsync(fixture, 1, "expired-session", "Expired");
        await AddAccountAsync(fixture, 2, "valid-session");
        fixture.Http.Handle = _ => throw new InvalidOperationException("Unexpected upstream request");
        using var service = CreateService(fixture);

        var error = await Assert.ThrowsAsync<InfrastructureException>(() => service.SearchRoomsAsync("", accountId: expired.Id));

        Assert.Equal("BILIBILI_ACCOUNT_EXPIRED", error.Code);
    }

    private static BilibiliService CreateService(IdentityFixture fixture)
    {
        return new(fixture.Context, fixture.Protector, fixture.Http, NullLogger<BilibiliService>.Instance, fixture.Settings, new BilibiliEventConnection(fixture.Http, NullLogger<BilibiliEventConnection>.Instance));
    }

    private static async Task<BilibiliAccount> AddAccountAsync(IdentityFixture fixture, long uid, string session, string status = "Valid")
    {
        var credentials = new BilibiliLoginCredentials { Cookie = $"SESSDATA={session};bili_jct=csrf;DedeUserID={uid};buvid3=test-device" };
        var account = new BilibiliAccount { Uid = uid, UserName = $"采集账号 {uid}", Status = status, ProtectedCredentials = fixture.Protector.Protect(JsonSerializer.Serialize(credentials)) };
        await fixture.Context.Collection<BilibiliAccount>("bilibili_accounts").InsertOneAsync(account);
        return account;
    }

    private static HttpResponseMessage Json(string content)
    {
        return new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    }
}
