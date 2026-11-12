namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>首次安装的浏览器请求。</summary>
public sealed record SetupRequest(string Token, string UserName, string Email, string Password, string MongoConnectionString, string DatabaseName, string StoragePath, string? FfmpegPath = null, string? FfprobePath = null);
