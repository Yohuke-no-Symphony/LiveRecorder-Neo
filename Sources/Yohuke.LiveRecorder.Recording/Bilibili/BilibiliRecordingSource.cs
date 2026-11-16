using Yohuke.DanmuCore.Apis;
using Yohuke.DanmuCore.Models.Accounts;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Bilibili;
using Yohuke.LiveRecorder.Application.Localization;

namespace Yohuke.LiveRecorder.Infrastructure.Bilibili;

/// <summary>Resolves live room metadata and FLV streams using supplied credentials without a database dependency.</summary>
public static class BilibiliRecordingSource
{
    /// <summary>Reads room state and the current broadcast identity using the supplied capture credentials.</summary>
    public static async Task<LiveRoomSnapshot> GetRoomAsync(BilibiliApi api, BilibiliLoginCredentials credentials, long roomId, CancellationToken cancellationToken)
    {
        var response = await api.GetRoomInfoWithCredentialsAsync(roomId, credentials.Cookie, cancellationToken: cancellationToken);
        RequireAuthenticatedResponse(response.Code);
        if (response.Code != 0 || response.Data?.RoomInfo is not { } room)
        {
            throw new InfrastructureException(MessageCode.BILIBILI_ROOM_UNAVAILABLE, 502, response.Code);
        }
        var anchor = response.Data.AnchorInfo?.BaseInfo;
        var started = room.LiveStartTime;
        if (room.LiveStatus == 1 && started <= 0)
        {
            var play = await api.GetRoomPlayInfoAsync(room.RoomId, cookie: credentials.Cookie, cancellationToken: cancellationToken);
            RequireAuthenticatedResponse(play.Code);
            if (play.Code != 0 || play.Data is null)
            {
                throw new InfrastructureException(MessageCode.BILIBILI_ROOM_UNAVAILABLE, 502, play.Code);
            }
            started = play.Data.LiveTime;
        }
        return new(room.RoomId, room.Uid, anchor?.Name ?? room.Uid.ToString(), NormalizeImage(anchor?.Face), room.Title ?? "直播录像", NormalizeImage(room.CoverUrl ?? room.UserCoverUrl), room.LiveStatus == 1, started is > 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(started).UtcDateTime : null);
    }

    /// <summary>Resolves the current playable FLV source and request headers.</summary>
    public static async Task<LiveStreamDescriptor> ResolveStreamAsync(BilibiliApi api, BilibiliLoginCredentials credentials, long roomId, int quality, CancellationToken cancellationToken)
    {
        var response = await api.GetRoomPlayInfoAsync(roomId, quality, credentials.Cookie, cancellationToken: cancellationToken);
        RequireAuthenticatedResponse(response.Code);
        if (response.Code != 0 || response.Data?.LiveStatus != 1)
        {
            throw new InfrastructureException(MessageCode.BILIBILI_STREAM_UNAVAILABLE, 503, response.Code);
        }
        var streams = NonNullItems(response.Data.PlayUrlInfo?.PlayUrl?.Streams);
        foreach (var stream in streams.Where(x => x.ProtocolName == "http_stream"))
        {
            foreach (var format in NonNullItems(stream.Formats).Where(x => x.FormatName == "flv"))
            {
                foreach (var codec in NonNullItems(format.Codecs).OrderByDescending(x => x.CodecName == "avc"))
                {
                    foreach (var host in NonNullItems(codec.UrlInfos))
                    {
                        var value = host.Host + codec.BaseUrl + host.Extra;
                        if (Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" && string.IsNullOrEmpty(url.UserInfo))
                        {
                            return new(value, new Dictionary<string, string> { ["User-Agent"] = BaseApi.DEFAULT_USER_AGENT, ["Referer"] = $"https://live.bilibili.com/{roomId}" }, codec.CurrentQuality, codec.CodecName, format.FormatName);
                        }
                    }
                }
            }
        }
        throw new InfrastructureException(MessageCode.BILIBILI_FLV_STREAM_MISSING, 503);
    }

    private static IEnumerable<T> NonNullItems<T>(IEnumerable<T?>? items) where T : class => items?.OfType<T>() ?? [];

    private static void RequireAuthenticatedResponse(int code)
    {
        if (code == -101)
        {
            throw new InfrastructureException(MessageCode.BILIBILI_ACCOUNT_EXPIRED, 409);
        }
    }

    private static string NormalizeImage(string? value) => string.IsNullOrWhiteSpace(value) ? ""
        : value.StartsWith("//", StringComparison.Ordinal) ? "https:" + value
        : value.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
}
