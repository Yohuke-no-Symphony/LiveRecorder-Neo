using System.Globalization;
using System.Text.Json.Nodes;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Configuration;

namespace Yohuke.LiveRecorder.Infrastructure.Files;

/// <summary>只从已归档事件与观众资料解析图片来源，不接受客户端提供的远端地址。</summary>
public sealed class EventArtworkService(SessionEventReader reader, IAppPaths paths, ArtworkCache artwork, AudienceAvatarService avatars)
{
    /// <summary>取得观众 UID 对应的本地缓存头像。</summary>
    public async Task<ArtworkCacheLease?> GetAudienceAvatarAsync(long userId, CancellationToken cancellationToken = default)
    {
        var source = await avatars.GetUrlAsync(userId, cancellationToken);
        return source is null ? null : await artwork.GetAsync(source, cancellationToken);
    }

    /// <summary>优先使用归档头像，旧归档缺少地址时按归档 UID 补齐。</summary>
    public async Task<ArtworkCacheLease?> GetAvatarAsync(BroadcastSession session, long sequence, CancellationToken cancellationToken = default)
    {
        var item = await ReadAsync(session, sequence, cancellationToken);
        if (item is null)
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(item.AvatarUrl))
        {
            var cached = await artwork.GetAsync(item.AvatarUrl, cancellationToken);
            if (cached is not null)
            {
                return cached;
            }
        }
        return long.TryParse(item.UserId, NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
            ? await GetAudienceAvatarAsync(userId, cancellationToken)
            : null;
    }

    /// <summary>按原始表情数组索引读取图片，分词和展示排序不会改变索引语义。</summary>
    public async Task<ArtworkCacheLease?> GetEmoticonAsync(BroadcastSession session, long sequence, int index, CancellationToken cancellationToken = default)
    {
        if (index < 0)
        {
            return null;
        }
        var item = await ReadAsync(session, sequence, cancellationToken);
        if (item?.Kind != "Danmu" || item.Payload?["emoticons"] is not JsonArray emoticons || index >= emoticons.Count ||
            emoticons[index] is not JsonObject emoticon || emoticon["url"] is not JsonValue value || !value.TryGetValue<string>(out var source))
        {
            return null;
        }
        return await artwork.GetAsync(source, cancellationToken);
    }

    private async Task<RecordedEventEnvelope?> ReadAsync(BroadcastSession session, long sequence, CancellationToken cancellationToken)
    {
        if (sequence <= 0)
        {
            return null;
        }
        var page = await reader.ReadAsync(session, paths.RecordsDirectory, 0, double.MaxValue, null, sequence - 1, 1, cancellationToken);
        return page.Items.FirstOrDefault(item => item.Sequence == sequence);
    }
}
