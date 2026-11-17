namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>发送身份验证邮件的基础设施端口。</summary>
public interface IEmailSender
{
    /// <summary>发送一封纯文本验证邮件。</summary>
    Task SendAsync(string email, string subject, string body, CancellationToken cancellationToken = default);
}
