using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

internal sealed class SessionEventTextFormatter(string language)
{
    internal string Title(bool translationsOnly) => translationsOnly
        ? Label("同传", "Live translations", "同時翻訳")
        : Label("弹幕", "Live chat", "コメント");

    internal string FormatHeader(BroadcastSession session, string title)
    {
        var start = session.StartedAtUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        var startLabel = Label("场次开始", "Session start", "配信開始");
        var offsetLabel = Label("方括号内为距场次开始的时间。", "Times in brackets are relative to the session start.", "角括弧内の時刻は配信開始からの経過時間です。");
        return $"{title} · {Indent(session.Title)}\n{startLabel} {start}\n{offsetLabel}\n";
    }

    internal string Empty(bool translationsOnly) => translationsOnly
        ? Label("没有匹配的同传。", "No matching translations.", "該当する同時翻訳がありません。")
        : Label("没有可导出的弹幕或互动。", "No chat or interactions to export.", "書き出せるコメントや交流がありません。");

    internal string? FormatEvent(RecordedEventEnvelope item, bool translationsOnly)
    {
        if (item.Kind == "Gap")
        {
            var gap = Label("录制中断", "Recording interruption", "録画の中断");
            return $"【{gap}】 {Indent(LocalizedText.Format(item.TextCode, item.Text ?? "", item.TextArguments))}";
        }

        var author = Author(item.UserName, item.UserId);
        if (translationsOnly)
        {
            return $"{author} · {Indent(item.Text)}";
        }

        var text = Indent(item.Text);
        return item.Kind switch
        {
            "Danmu" => $"【{Title(false)}】 {author} · {text}",
            "SuperChat" => $"【SC】 {Join(author, Amount(item), text)}",
            "Gift" => $"【{Label("礼物", "Gift", "ギフト")}】 {Join(author, Gift(item, text), Amount(item))}",
            "Guard" => $"【{Label("大航海", "Membership", "大航海")}】 {Join(author, Guard(item, text), Amount(item))}",
            "RedPocket" => RedPocket(item),
            _ => null
        };
    }

    private string Gift(RecordedEventEnvelope item, string text)
    {
        var name = ReadValue(item.Payload, "giftName");
        return name.Length > 0 ? $"{Indent(name)} ×{Math.Max(1, item.Quantity)}" : text;
    }

    private string Guard(RecordedEventEnvelope item, string text)
    {
        var name = ReadValue(item.Payload, "guardLevel") switch
        {
            "1" => Label("总督", "Governor", "総督"),
            "2" => Label("提督", "Admiral", "提督"),
            "3" => Label("舰长", "Captain", "艦長"),
            _ => ReadValue(item.Payload, "guardLevelName")
        };
        return $"{(name.Length > 0 ? Indent(name) : text)} ×{Math.Max(1, item.Quantity)}";
    }

    private string Amount(RecordedEventEnvelope item)
    {
        if (item.Kind == "Gift" && (item.PriceSource == "NonMonetary" || item.CoinType == "silver"))
        {
            return Label("银瓜子礼物", "Silver-seed gift", "銀瓜子のギフト");
        }
        if (item.Amount is not { } amount)
        {
            return Label("金额未知", "Amount unknown", "金額不明");
        }
        var value = "¥" + amount.ToString("0.############################", CultureInfo.InvariantCulture);
        return item.Kind == "Guard" && (item.PriceSource == "ProtocolQuote" || item.Command == "GUARD_BUY")
            ? $"{Label("参考金额", "Reference amount", "参考金額")} {value}"
            : value;
    }

    private string RedPocket(RecordedEventEnvelope item)
    {
        var pocket = item.Payload?["redPocket"] as JsonObject;
        var id = string.IsNullOrEmpty(item.TransactionId) ? ReadValue(pocket, "lotId") : item.TransactionId;
        var senderName = string.IsNullOrEmpty(item.UserName) ? ReadValue(pocket, "senderName") : item.UserName;
        var senderId = string.IsNullOrEmpty(item.UserId) ? ReadValue(pocket, "senderUid") : item.UserId;
        var author = ReadFlag(pocket, "isMystery") ? Mystery() : Author(senderName, senderId);
        var hasResult = ReadFlag(pocket, "hasResult");
        var status = hasResult ? Label("已开奖", "Drawn", "抽選済み") : ReadFlag(item.Payload, "isDrawn")
            ? Label("已开奖，名单未获取", "Drawn, results unavailable", "抽選済み・結果未取得")
            : Label("待开奖", "Awaiting draw", "抽選待ち");
        var drawTime = long.TryParse(ReadValue(pocket, "endTime"), CultureInfo.InvariantCulture, out var seconds) && seconds is > 0 and <= 253402300799
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
            : "";

        var battery = ReadValue(pocket, "totalBattery");
        var protocolPrice = battery.Length == 0 ? ReadValue(pocket, "totalPrice") : "";
        var text = new StringBuilder(Join(
            $"【{Label("红包", "Red pocket", "お年玉")}】 {author}",
            id == "0" ? "" : Field(Label("红包编号", "Pocket ID", "お年玉 ID"), Indent(id)),
            status,
            Indent(item.Text),
            Field(Label("总电池", "Total batteries", "電池合計"), battery),
            Field(Label("红包总价（单位未知）", "Total value (unit unknown)", "合計金額（単位不明）"), protocolPrice),
            Field(Label("奖品总数", "Total prizes", "賞品数"), ReadValue(pocket, "totalAwardCount")),
            Field(Label("开奖时间", "Draw time", "抽選時刻"), drawTime)));

        var awards = (pocket?["awards"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        if (awards.Length > 0)
        {
            var prizes = string.Join(" / ", awards.Select(award => Award(award)));
            text.Append("\n    ").Append(Field(Label("奖品", "Prizes", "賞品"), prizes));
        }
        if (hasResult && pocket?["winners"] is JsonArray { Count: > 0 } winners)
        {
            text.Append("\n    ").Append(Label("中奖名单", "Winners", "当選者"));
            foreach (var winner in winners.OfType<JsonObject>())
            {
                var name = ReadFlag(winner, "isMystery") ? Mystery() : Author(ReadValue(winner, "userName"), ReadValue(winner, "uid"));
                text.Append("\n      ").Append(Join(name, Award(winner, awards)));
            }
        }
        return text.ToString();
    }

    private string Award(JsonObject item, JsonObject[]? awards = null)
    {
        var name = ReadValue(item, "giftName");
        if (name.Length == 0)
        {
            name = ReadValue(item, "name");
        }
        var id = ReadValue(item, "giftId");
        if (name.Length == 0 && id.Length > 0)
        {
            var matchingAward = awards?.FirstOrDefault(award => ReadValue(award, "giftId") == id);
            name = ReadValue(matchingAward, "name");
            if (name.Length == 0)
            {
                name = Field(Label("礼物编号", "Gift ID", "ギフト ID"), id);
            }
        }
        var quantity = ReadValue(item, "quantity");
        var award = name.Length > 0 && quantity.Length > 0 ? $"{Indent(name)} ×{quantity}" : Indent(name);
        var battery = Field(Label("电池", "Batteries", "電池"), ReadValue(item, "batteryAmount"));
        var intimacy = Field(Label("亲密度", "Affinity", "親密度"), ReadValue(item, "intimacyAmount"));
        return Join(award, battery, intimacy);
    }

    private string Author(string? name, string? id)
    {
        var display = Indent(name);
        var uid = id is null or "" or "0" ? "" : $"UID {Indent(id)}";
        if (display.Length == 0)
        {
            return uid.Length > 0 ? uid : Label("未知用户", "Unknown user", "不明なユーザー");
        }
        return uid.Length > 0 ? $"{display} ({uid})" : display;
    }

    private string Mystery() => Label("神秘人", "Anonymous user", "匿名ユーザー");

    private string Label(string chinese, string english, string japanese) => language switch
    {
        "en" => english,
        "ja" => japanese,
        _ => chinese
    };

    private static string Field(string label, string value) => value.Length == 0 ? "" : $"{label} {value}";

    private static string Join(params string[] values) => string.Join(" · ", values.Where(value => value.Length > 0));

    private static string Indent(string? value) => value?.ReplaceLineEndings("\n    ") ?? "";

    private static bool ReadFlag(JsonObject? data, string key) =>
        data?[key] is JsonValue value && value.TryGetValue<bool>(out var result) && result;

    private static string ReadValue(JsonObject? data, string key) => data?[key] is JsonValue value
        ? value.TryGetValue<string>(out var text) ? text : value.GetValueKind() == JsonValueKind.Number ? value.ToJsonString() : ""
        : "";
}
