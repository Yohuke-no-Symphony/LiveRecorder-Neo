namespace Yohuke.LiveRecorder.Infrastructure.Persistence.Migrations;

/// <summary>声明迁移器处理的起始与目标数据版本。</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MigrationAttribute(string from, string to) : Attribute
{
    public string From { get; } = from;
    public string To { get; } = to;
}
