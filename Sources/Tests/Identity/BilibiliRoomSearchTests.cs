using System.Net;
using System.Text;
using System.Text.Json;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Infrastructure.Bilibili;
using Xunit;

namespace Yohuke.LiveRecorder.Identity.Tests;

/// <summary>验证 Core 搜索适配、分页、凭据边界及取消行为。</summary>
public sealed class BilibiliRoomSearchTests
{
    [Fact]
    public async Task NicknameSearchPreservesPaginationAndMapsPlainPublicMetadata()
    {
        using var handler = new RoomSearchHttpHandler((request, _) =>
        {
            Assert.Contains("page=3", request.RequestUri!.Query);
            Assert.Contains("SESSDATA=private-session", request.Headers.GetValues("Cookie").Single());
            return Task.FromResult(Json("""{"code":0,"data":{"page":3,"numPages":4,"result":[{"uid":3546873520458421,"roomid":21573665,"uname":"<em>猫</em>&amp;主播","title":"<b>直播标题</b>","is_live":true},{"uid":3546873520458421,"roomid":21573665,"uname":"重复"},{"uid":0,"roomid":1}]}}"""));
        });
        using var http = new HttpClient(handler);
        var service = new BilibiliRoomSearchService(http);

        var page = await service.SearchAsync(" 猫 ", 3, "SESSDATA=private-session;buvid3=test-device");

        var room = Assert.Single(page.Results);
        Assert.Equal("21573665", room.RoomId);
        Assert.Equal("3546873520458421", room.OwnerUid);
        Assert.Equal("猫&主播", room.OwnerName);
        Assert.Equal("直播标题", room.Title);
        Assert.True(room.IsLive);
        Assert.Equal(["nickname"], room.Sources);
        Assert.Equal(3, page.Page);
        Assert.True(page.HasMore);
        Assert.DoesNotContain("private-session", JsonSerializer.Serialize(page));
    }

    [Fact]
    public async Task EmptyAnonymousSearchReturnsStructuredLoginIssueWithoutNetwork()
    {
        using var handler = new RoomSearchHttpHandler((_, _) => throw new InvalidOperationException("Unexpected network request"));
        using var http = new HttpClient(handler);

        var page = await new BilibiliRoomSearchService(http).SearchAsync("");

        Assert.Empty(page.Results);
        var issue = Assert.Single(page.Issues);
        Assert.Equal("followingLoginRequired", issue.Code);
        Assert.Equal(-101, issue.ApiCode);
        Assert.Equal(["following"], issue.Sources);
    }

    [Fact]
    public async Task NumericSearchKeepsRoomResultAlongsideSanitizedUidFailure()
    {
        using var handler = new RoomSearchHttpHandler((request, _) => Task.FromResult(Json(request.RequestUri!.AbsolutePath switch
        {
            "/x/web-interface/nav" => """{"code":-101,"data":{"wbi_img":{"img_url":"https://i0.hdslb.com/7cd084941338484aae1ad9425b84077c.png","sub_url":"https://i0.hdslb.com/4932caff0ff746eab6f01bf08b70ac45.png"}}}""",
            "/room/v1/Room/getRoomInfoOld" => """{"code":-412,"message":"blocked private-session"}""",
            _ => """{"code":0,"data":{"room_info":{"room_id":21573665,"short_id":42,"uid":123,"title":"直播标题"},"anchor_info":{"base_info":{"uname":"主播"}}}}"""
        })));
        using var http = new HttpClient(handler);

        var page = await new BilibiliRoomSearchService(http).SearchAsync("42", cookie: "SESSDATA=private-session");

        Assert.Equal("21573665", Assert.Single(page.Results).RoomId);
        Assert.Equal("42", page.Results[0].ShortRoomId);
        Assert.Equal(["roomId"], page.Results[0].Sources);
        var issue = Assert.Single(page.Issues);
        Assert.Equal(-412, issue.ApiCode);
        Assert.Equal(["uid"], issue.Sources);
        Assert.Contains("blocked", issue.Message);
        Assert.DoesNotContain("private-session", JsonSerializer.Serialize(page));
    }

    [Fact]
    public async Task InvalidParametersFailBeforeNetwork()
    {
        using var handler = new RoomSearchHttpHandler((_, _) => throw new InvalidOperationException("Unexpected network request"));
        using var http = new HttpClient(handler);
        var service = new BilibiliRoomSearchService(http);

        var error = await Assert.ThrowsAsync<InfrastructureException>(() => service.SearchAsync("主播", 0));

        Assert.Equal(MessageCode.BILIBILI_SEARCH_INVALID.ToString(), error.Code);
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public async Task UpstreamFailureDoesNotExposeRemoteCredentials()
    {
        using var handler = new RoomSearchHttpHandler((_, _) => Task.FromResult(Json("""{"code":-412,"message":"blocked private-session"}""")));
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<InfrastructureException>(() =>
            new BilibiliRoomSearchService(http).SearchAsync("主播", cookie: "SESSDATA=private-session"));

        Assert.Equal(MessageCode.BILIBILI_SEARCH_UNAVAILABLE.ToString(), error.Code);
        Assert.Equal(502, error.StatusCode);
        Assert.DoesNotContain("private-session", error.ToString());
    }

    [Fact]
    public async Task RequestCancellationPropagatesInsteadOfBecomingSearchFailure()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new RoomSearchHttpHandler((_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(token);
        });
        using var http = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BilibiliRoomSearchService(http).SearchAsync("主播", cancellationToken: cancellation.Token));
    }

    private static HttpResponseMessage Json(string content)
    {
        return new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    }
}
