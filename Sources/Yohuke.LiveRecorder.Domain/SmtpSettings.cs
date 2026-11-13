namespace Yohuke.LiveRecorder.Domain;

/// <summary>发件服务器配置，密码仅保存保护后的值。</summary>
public sealed class SmtpSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; }
    /// <summary>是否检查 TLS 证书吊销状态，默认开启。</summary>
    public bool CheckCertificateRevocation { get; set; } = true;
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "Live Recorder";
}
