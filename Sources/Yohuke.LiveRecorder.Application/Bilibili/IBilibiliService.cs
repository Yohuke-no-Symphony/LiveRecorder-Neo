using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Application.Bilibili;

/// <summary>Core 的服务端适配，包括账号管理与录制来源。</summary>
public interface IBilibiliService
{
    /// <summary>按昵称、UID、房号搜索；空关键词查询所选账号正在直播的关注。</summary>
    Task<BilibiliRoomSearchPage> SearchRoomsAsync(string keyword, int page = 1, string? accountId = null, CancellationToken cancellationToken = default);
    /// <summary>读取主播名称、头像、封面及开播状态。</summary>
    Task<LiveRoomSnapshot> GetRoomAsync(long roomId, string? accountId = null, CancellationToken cancellationToken = default);
    /// <summary>解析允许采集的实际流和清晰度。</summary>
    Task<LiveStreamDescriptor> ResolveStreamAsync(long roomId, int quality, string? accountId = null, CancellationToken cancellationToken = default);
    /// <summary>通过同一条弹幕连接顺序消费可归档业务事件。</summary>
    Task SubscribeEventsAsync(long roomId, string? accountId, Func<RecordedEventEnvelope, CancellationToken, Task> onEvent, CancellationToken cancellationToken = default);
    /// <summary>列出管理员绑定的采集账号，不返回凭据。</summary>
    Task<IReadOnlyList<BilibiliAccountSummary>> ListAccountsAsync(CancellationToken cancellationToken = default);
    /// <summary>设置首选采集账号；传空恢复自动选择。</summary>
    Task<IReadOnlyList<BilibiliAccountSummary>> SetDefaultAccountAsync(string? accountId, CancellationToken cancellationToken = default);
    /// <summary>创建二维码登录流程。</summary>
    Task<BilibiliLoginSession> BeginQrAsync(CancellationToken cancellationToken = default);
    /// <summary>为指定账号创建二维码重新登录流程。</summary>
    Task<BilibiliLoginSession> BeginAccountQrAsync(string accountId, CancellationToken cancellationToken = default);
    /// <summary>轮询二维码状态并在成功后保存凭据。</summary>
    Task<BilibiliLoginSession> PollQrAsync(string sessionId, CancellationToken cancellationToken = default);
    /// <summary>创建短信流程与平台验证码挑战。</summary>
    Task<BilibiliLoginSession> BeginSmsAsync(BilibiliSmsRequest request, CancellationToken cancellationToken = default);
    /// <summary>为指定账号创建短信重新登录流程。</summary>
    Task<BilibiliLoginSession> BeginAccountSmsAsync(string accountId, BilibiliSmsRequest request, CancellationToken cancellationToken = default);
    /// <summary>提交用户完成的挑战并请求发送短信。</summary>
    Task<BilibiliLoginSession> SendSmsAsync(string sessionId, BilibiliChallengeResponse response, CancellationToken cancellationToken = default);
    /// <summary>校验短信验证码并保存成功绑定的凭据。</summary>
    Task<BilibiliLoginSession> CompleteSmsAsync(string sessionId, string code, CancellationToken cancellationToken = default);
    /// <summary>删除绑定的采集账号。</summary>
    Task DeleteAccountAsync(string id, CancellationToken cancellationToken = default);
}
