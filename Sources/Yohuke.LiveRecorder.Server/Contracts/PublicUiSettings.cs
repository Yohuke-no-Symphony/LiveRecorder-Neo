namespace Yohuke.LiveRecorder.Server.Contracts;

/// <summary>允许匿名读取的界面设置，不包含管理配置。</summary>
public sealed record PublicUiSettings(string DefaultLanguage);
