namespace Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

internal sealed record WorkerConnectionOptions
{
    public string ApiBaseUrl { get; set; } = "https://localhost:4784";
    public string ControlHost { get; set; } = "localhost";
    public int ControlPort { get; set; } = 4790;
    public string WorkerId { get; set; } = "";
    public string Secret { get; set; } = "";
    public string? TlsServerName { get; set; }
    public string? ServerCertificateSha256 { get; set; }
    public string? ControlCertificateSha256 { get; set; }
    public string? ApiCertificateSha256 { get; set; }

    internal void Validate()
    {
        if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException("Worker.ApiBaseUrl 必须是没有凭据或查询参数的 HTTPS 主站地址");
        }
        if (string.IsNullOrWhiteSpace(ControlHost) || ControlPort is < 1 or > 65535
            || string.IsNullOrWhiteSpace(WorkerId) || string.IsNullOrWhiteSpace(Secret) || Secret.Length > 256)
        {
            throw new InvalidDataException("请运行 --bind 配置主站地址、控制端口、Worker ID 和 Secret，或修改 settings.json；Secret 不能超过 256 个字符");
        }
        foreach (var pin in new[] { ServerCertificateSha256, ControlCertificateSha256, ApiCertificateSha256 })
        {
            if (!string.IsNullOrEmpty(pin) && (pin.Length != 64 || pin.Any(character => !Uri.IsHexDigit(character))))
            {
                throw new InvalidDataException("TLS 证书 SHA-256 指纹必须是 64 位十六进制字符串");
            }
        }
    }
}
