namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>持有一次性安装令牌的初次部署参数。</summary>
public sealed record InstallRequest(string InstallationToken, string AdminEmail, string AdminPassword, string AdminDisplayName, string StoragePath, string MongoConnectionString, string DatabaseName);
