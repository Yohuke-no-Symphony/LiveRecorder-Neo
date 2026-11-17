namespace Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

internal sealed record WorkerConnectionOptions
{
    public string ApiBaseUrl { get; set; } = "https://localhost:4784";
    public string ControlHost { get; set; } = "localhost";
    public int ControlPort { get; set; } = 4790;
    public bool ControlUseTls { get; set; } = true;
    public string WorkerId { get; set; } = "";
    public string Secret { get; set; } = "";
    public string? TlsServerName { get; set; }
    public string? ServerCertificateSha256 { get; set; }
    public string? ControlCertificateSha256 { get; set; }
    public string? ApiCertificateSha256 { get; set; }

    internal void Validate()
    {
        if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException("Worker.ApiBaseUrl must be an HTTP or HTTPS server URL without credentials, query parameters or a fragment.");
        }
        if (string.IsNullOrWhiteSpace(ControlHost) || ControlPort is < 1 or > 65535
            || string.IsNullOrWhiteSpace(WorkerId) || string.IsNullOrWhiteSpace(Secret) || Secret.Length > 256)
        {
            throw new InvalidDataException("Run 'register' to configure the server address, control port, worker ID and secret, or edit settings.json. The secret must contain at most 256 characters.");
        }
        foreach (var pin in new[] { ServerCertificateSha256, ControlCertificateSha256, ApiCertificateSha256 })
        {
            if (!string.IsNullOrEmpty(pin) && (pin.Length != 64 || pin.Any(character => !Uri.IsHexDigit(character))))
            {
                throw new InvalidDataException("TLS certificate SHA-256 fingerprints must contain exactly 64 hexadecimal characters.");
            }
        }
    }
}
