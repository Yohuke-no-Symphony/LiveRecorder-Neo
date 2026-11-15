namespace Yohuke.LiveRecorder.Server.Configuration;

/// <summary>独立 Worker TCP 或 TLS 控制端口的主站启动配置。</summary>
public sealed class TranscodingControlOptions
{
    public bool Enabled { get; set; }
    public bool UseTls { get; set; } = true;
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 4790;
    public string? CertificatePath { get; set; } = "";
    public string? CertificatePassword { get; set; }
    public int MaximumConnections { get; set; } = 128;
}
