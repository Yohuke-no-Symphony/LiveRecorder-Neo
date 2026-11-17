using Newtonsoft.Json;
using Yohuke.DanmuCore.Errors;
using Yohuke.DanmuCore.Models.Search;
using Yohuke.DanmuCore.Services;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Bilibili;
using Yohuke.LiveRecorder.Application.Localization;

namespace Yohuke.LiveRecorder.Infrastructure.Bilibili;

/// <summary>将 Core 的昵称、UID、房号和关注查询转换为录播应用的搜索契约。</summary>
public sealed class BilibiliRoomSearchService(HttpClient client)
{
    private const int MAX_KEYWORD_LENGTH = 200;
    private static readonly IReadOnlyDictionary<LiveRoomSearchSource, string> sourceNames = new Dictionary<LiveRoomSearchSource, string>
    {
        [LiveRoomSearchSource.Nickname] = "nickname",
        [LiveRoomSearchSource.UserId] = "uid",
        [LiveRoomSearchSource.RoomId] = "roomId",
        [LiveRoomSearchSource.Following] = "following"
    };

    /// <summary>执行公开搜索；空关键词使用明确传入的后台 Cookie 读取正在直播的关注。</summary>
    public async Task<BilibiliRoomSearchPage> SearchAsync(string keyword, int page = 1, string? cookie = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (page < 1 || keyword.Length > MAX_KEYWORD_LENGTH)
        {
            throw new InfrastructureException(MessageCode.BILIBILI_SEARCH_INVALID);
        }

        try
        {
            var response = await new BilibiliLiveRoomSearchService(client).SearchAsync(keyword.Trim(), page, cookie, cancellationToken: cancellationToken);
            return new(
                response.Results.Select(room => new BilibiliRoomSearchResult(room.RoomId, room.ShortRoomId, room.OwnerUid, room.OwnerName, room.Title, room.IsLive, Sources(room.Sources))).ToArray(),
                response.Issues.Select(issue => new BilibiliRoomSearchIssue(Sources(issue.Source), issue.ApiCode, issue.Error?.Code switch
                {
                    CoreErrorCode.RoomSearchFollowingLoginRequired => "followingLoginRequired",
                    CoreErrorCode.RoomSearchInvalidIdentifier => "invalidIdentifier",
                    CoreErrorCode.RoomSearchResultsMissing => "incompleteResponse",
                    null => null,
                    _ => "unavailable"
                }, issue.Message)).ToArray(),
                response.Page, response.HasMore);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or InvalidOperationException or JsonException)
        {
            throw new InfrastructureException(MessageCode.BILIBILI_SEARCH_UNAVAILABLE, 502);
        }
    }

    private static IReadOnlyList<string> Sources(LiveRoomSearchSource sources)
    {
        return sourceNames.Where(pair => sources.HasFlag(pair.Key)).Select(pair => pair.Value).ToArray();
    }
}
