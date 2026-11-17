using Yohuke.LiveRecorder.Application.Localization;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Auth;

namespace Yohuke.LiveRecorder.Infrastructure.Auth;

/// <summary>从受保护配置读取密码并发送验证邮件。</summary>
public sealed class SmtpEmailSender(ISettingsStore settingsStore, ISecretProtector protector) : IEmailSender
{
    /// <inheritdoc />
    public async Task SendAsync(string email, string subject, string body, CancellationToken cancellationToken = default)
    {
        var settings = (await settingsStore.GetAsync(cancellationToken)).Smtp;
        if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.FromAddress) || settings.Port is < 1 or > 65535)
        {
            throw new InfrastructureException(MessageCode.SMTP_CONFIGURATION_INVALID, 503);
        }
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        message.To.Add(new MailboxAddress("", IdentityValidation.NormalizeEmail(email)));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };
        using var client = new SmtpClient();
        client.Timeout = 15000;
        client.CheckCertificateRevocation = settings.CheckCertificateRevocation;
        await client.ConnectAsync(settings.Host, settings.Port, settings.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable, cancellationToken);
        if (!string.IsNullOrWhiteSpace(settings.UserName))
        {
            if (!client.IsSecure)
            {
                throw new InfrastructureException(MessageCode.SMTP_TLS_REQUIRED, 503);
            }
            await client.AuthenticateAsync(settings.UserName, protector.Unprotect(settings.Password), cancellationToken);
        }
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}
