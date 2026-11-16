using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Yohuke.DanmuCore.Apis;
using Yohuke.DanmuCore.Apis.Models.Bilibili;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Bilibili;

/// <summary>将共享协议投影为可读文件事件，保留未知包和原始 JSON。</summary>
public static class BilibiliEventMapper
{
    private static JsonSerializerSettings PayloadJson { get; } = new() { ContractResolver = new CamelCasePropertyNamesContractResolver() };

    /// <summary>解析一个原始业务包，批量礼物可产生多个投影。</summary>
    public static IReadOnlyList<RecordedEventEnvelope> Map(BilibiliRawBusinessEvent source)
    {
        var command = source.Command.Split(':', 2)[0];
        JObject? json = null;
        try
        {
            json = JObject.Parse(source.RawJson);
            if (command == "DANMU_MSG" && BilibiliLiveWebSocket.ParseDanmuMessage(source.RoomId, json) is { } danmu)
            {
                return [MapDanmu(danmu, source.ReceivedAtUtc, source.Command, source.RawJson)];
            }
            if (command is "SEND_GIFT" or "SEND_GIFT_V2")
            {
                var gifts = BilibiliBusinessEventParser.ParseGifts(source.RoomId, json);
                if (gifts.Count > 0)
                {
                    return gifts.Select(gift =>
                    {
                        var result = Create(source, "Gift", gift.MessageId, gift.Timestamp, gift.Uid, gift.UserName, $"{gift.GiftName} ×{gift.Quantity}", gift);
                        result.Quantity = gift.Quantity;
                        var isGold = gift.CoinType.Equals("gold", StringComparison.OrdinalIgnoreCase);
                        var knownPrice = command == "SEND_GIFT_V2" || decimal.TryParse(json["data"]?["price"]?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var price) && price >= 0;
                        result.Amount = isGold && knownPrice ? gift.UnitPrice * gift.Quantity / 1000m : null;
                        result.CoinType = gift.CoinType;
                        result.TransactionId = gift.TransactionId;
                        result.PriceSource = !isGold ? "NonMonetary" : knownPrice ? "ProtocolGold" : "Unknown";
                        return result;
                    }).ToArray();
                }
            }
            if (command == "SUPER_CHAT_MESSAGE" && BilibiliLiveWebSocket.ParseSuperChatMessage(source.RoomId, json) is { } sc)
            {
                var result = Create(source, "SuperChat", sc.MessageId, sc.Timestamp, sc.Uid, sc.UserName, sc.Content, sc);
                result.Amount = sc.Price;
                result.PriceSource = "ProtocolPaid";
                result.AvatarUrl = json["data"]?["user_info"]?.Value<string>("face");
                return [result];
            }
            if ((command == "GUARD_BUY" || command.StartsWith("USER_TOAST_MSG", StringComparison.Ordinal)) && BilibiliLiveWebSocket.ParseGuardPurchaseMessage(source.RoomId, json) is { } guard)
            {
                var result = Create(source, "Guard", guard.Identity, guard.Timestamp, guard.Uid, guard.UserName, guard.GuardLevelName, guard);
                result.Quantity = Math.Max(1, guard.Quantity);
                result.Amount = guard.TotalPrice is >= 0 ? guard.TotalPrice : null;
                result.PriceSource = result.Amount is null ? "Unknown" : command == "GUARD_BUY" ? "ProtocolQuote" : "ProtocolPaid";
                return [result];
            }
            if (command.StartsWith("POPULARITY_RED_POCKET", StringComparison.Ordinal) && BilibiliBusinessEventParser.ParseRedPocket(source.RoomId, json) is { } red)
            {
                return [MapRedPocket(red, source.ReceivedAtUtc, source.Command, source.RawJson)];
            }
            if (command.StartsWith("RECORDER_", StringComparison.Ordinal))
            {
                var isGap = command.Contains("DISCONNECTED", StringComparison.Ordinal) || command.Contains("FAILED", StringComparison.Ordinal) || command.Contains("INCOMPLETE", StringComparison.Ordinal);
                var result = Create(source, isGap ? "Gap" : "Recovery", "", 0, 0, "", json.Value<string>("message") ?? (isGap ? "弹幕连接中断，缺失范围未知" : "弹幕连接恢复，历史仅可有限补录"), json);
                return [result];
            }
            if (command is "LIVE" or "PREPARING" or "ROOM_CHANGE" or "ROOM_REAL_TIME_MESSAGE_UPDATE")
            {
                return [Create(source, "RoomState", "", 0, 0, "", command, json)];
            }
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidDataException or OverflowException or ArgumentException)
        {
            var failed = Create(source, "Raw", "", 0, 0, "", "", json ?? new JObject());
            failed.Payload!["parseError"] = exception.GetType().Name;
            return [failed];
        }
        return [Create(source, "Raw", "", 0, 0, "", "", json)];
    }

    /// <summary>保留原始毫秒并标记近期历史投影来源。</summary>
    public static RecordedEventEnvelope MapDanmu(BilibiliDanmuMessage message, DateTimeOffset receivedAt, string command = "RECORDER_HISTORY_DANMU", string? rawJson = null)
    {
        var history = rawJson is null;
        var source = new BilibiliRawBusinessEvent(message.RoomId, command, rawJson ?? JsonConvert.SerializeObject(new { cmd = command, normalized = message }, PayloadJson), receivedAt);
        var result = Create(source, "Danmu", message.MessageId, message.Timestamp, message.Uid, message.UserName, message.Content, message);
        if (message.TimestampMilliseconds is > 0 and <= 253402300799999)
        {
            result.SourceTimeUtc = DateTimeOffset.FromUnixTimeMilliseconds(message.TimestampMilliseconds).UtcDateTime;
        }
        result.Payload!["isHistory"] = history;
        return result;
    }

    /// <summary>红包阶段按实际观察时间追加，源包时间保存在 Payload 中。</summary>
    public static RecordedEventEnvelope MapRedPocket(BilibiliRedPocketMessage message, DateTimeOffset receivedAt, string command, string? rawJson = null)
    {
        var json = rawJson ?? JsonConvert.SerializeObject(new { cmd = command, normalized = message }, PayloadJson);
        var result = Create(new(message.RoomId, command, json, receivedAt), "RedPocket", $"{message.RedPocket.LotId}:{Hash(json)}", 0, message.RedPocket.SenderUid, message.RedPocket.SenderName, message.RedPocket.Danmu, message);
        result.TransactionId = message.RedPocket.LotId.ToString(CultureInfo.InvariantCulture);
        result.AvatarUrl = message.RedPocket.SenderAvatarUrl;
        return result;
    }

    private static RecordedEventEnvelope Create(BilibiliRawBusinessEvent source, string kind, string id, long timestamp, long uid, string userName, string text, object payload) => new()
    {
        EventId = string.IsNullOrWhiteSpace(id) ? "" : $"{kind}:{source.RoomId}:{id}",
        Kind = kind,
        Command = source.Command,
        ReceivedAtUtc = source.ReceivedAtUtc.UtcDateTime,
        SourceTimeUtc = timestamp is > 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime : null,
        UserId = uid > 0 ? uid.ToString(CultureInfo.InvariantCulture) : "",
        UserName = userName,
        Text = text,
        Payload = JsonNode.Parse(JsonConvert.SerializeObject(payload, PayloadJson)) as JsonObject ?? new(),
        RawJson = source.RawJson
    };

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
