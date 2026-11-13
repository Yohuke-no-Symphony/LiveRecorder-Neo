using Yohuke.LiveRecorder.Application.Auth;

namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>首次安装的浏览器请求，两个目录均以唯一设置文件为相对路径基准。</summary>
public sealed record SetupRequest(string Token, string UserName, string Email, string Password, string MongoConnectionString, string DatabaseName, string DataDirectory, string RecordsDirectory, InstallationMode Mode = InstallationMode.New, string? FfmpegPath = null, string? FfprobePath = null);
