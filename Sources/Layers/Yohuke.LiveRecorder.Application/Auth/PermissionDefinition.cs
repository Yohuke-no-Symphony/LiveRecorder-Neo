namespace Yohuke.LiveRecorder.Application.Auth;

/// <summary>权限目录中供授权策略与设置面板共用的定义。</summary>
public sealed record PermissionDefinition(
    string Id,
    string Group,
    string LabelKey,
    string? DescriptionKey,
    string Icon,
    bool IsDangerous,
    bool DefaultGranted);
