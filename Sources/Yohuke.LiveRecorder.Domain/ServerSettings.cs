namespace Yohuke.LiveRecorder.Domain;

/// <summary>管理员可调整的录制、媒体和注册设置。</summary>
public sealed class ServerSettings : Entity
{
    public ServerSettings()
    {
        Id = "server";
    }

    public string DefaultLanguage { get; set; } = Localization.SupportedLanguages.DEFAULT;
    public string TimeZone { get; set; } = "Asia/Shanghai";
    public int Quality { get; set; } = 10000;
    public bool RecordDanmu { get; set; } = true;
    public long SegmentBytes { get; set; } = 4L * 1024 * 1024 * 1024;
    public int ClipRetentionDays { get; set; } = 30;
    public int TrashRetentionDays { get; set; } = 7;
    public long CacheLimitBytes { get; set; } = 20L * 1024 * 1024 * 1024;
    public int CacheRetentionMinutes { get; set; } = 60;
    public long MinimumFreeBytes { get; set; } = 5L * 1024 * 1024 * 1024;
    public int MediaConcurrency { get; set; } = 1;
    public int PlaybackConcurrency { get; set; } = 2;
    public int MonitorIntervalSeconds { get; set; } = 30;
    public int SessionGraceMinutes { get; set; } = 10;
    public bool RegistrationEnabled { get; set; }
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public SmtpSettings Smtp { get; set; } = new();
    [System.Text.Json.Serialization.JsonPropertyName("oauthProviders")]
    public List<OAuthProviderSettings> OAuthProviders { get; set; } = [];
}
