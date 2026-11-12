namespace Yohuke.LiveRecorder.Domain;

/// <summary>由管理员绑定的 B站采集身份，凭据始终受保护保存。</summary>
public sealed class BilibiliAccount : Entity
{
    public long Uid { get; set; }
    public string UserName { get; set; } = "";
    public string AvatarUrl { get; set; } = "";
    public string ProtectedCredentials { get; set; } = "";
    public string Status { get; set; } = "Valid";
    public DateTime? LastValidatedAtUtc { get; set; }
}
