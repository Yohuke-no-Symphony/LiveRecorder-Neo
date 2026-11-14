namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>通过登记邮箱的单次链接恢复本站密码。</summary>
public interface IPasswordResetService
{
    /// <summary>读取不暴露邮件或站点配置的公开恢复能力。</summary>
    Task<PasswordResetOptions> GetOptionsAsync(CancellationToken cancellationToken = default);

    /// <summary>请求恢复邮件，对未知账号和重发冷却返回相同结果。</summary>
    Task RequestAsync(PasswordResetRequest request, CancellationToken cancellationToken = default);

    /// <summary>原子消费恢复授权并撤销旧会话，不改变账号审核或封禁状态。</summary>
    Task CompleteAsync(PasswordResetCompleteRequest request, CancellationToken cancellationToken = default);
}
