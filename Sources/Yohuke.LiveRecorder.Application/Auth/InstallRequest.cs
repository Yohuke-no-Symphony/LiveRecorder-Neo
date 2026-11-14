namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>持有一次性安装令牌的初次部署参数，仅全新安装需要管理员信息。</summary>
public sealed record InstallRequest(string InstallationToken, string? AdminEmail, string? AdminPassword, string? AdminDisplayName, string MongoConnectionString, string DatabaseName, string DataDirectory, string RecordsDirectory, InstallationMode Mode = InstallationMode.New, string? FfmpegPath = null, string? FfprobePath = null);
