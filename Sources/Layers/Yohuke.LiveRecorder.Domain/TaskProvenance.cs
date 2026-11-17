namespace Yohuke.LiveRecorder.Domain;

/// <summary>区分任务的发起来源与执行节点，保留发起时的用户身份。</summary>
public sealed record TaskProvenance
{
    public string Origin { get; init; } = "Legacy";
    public string? InitiatorUserId { get; init; }
    public string? InitiatorName { get; init; }

    /// <summary>记录由已认证用户显式发起的任务。</summary>
    public static TaskProvenance User(string id, string name) => new()
    {
        Origin = "User", InitiatorUserId = id, InitiatorName = name
    };

    /// <summary>记录由系统调度发起的任务。</summary>
    public static TaskProvenance Automatic() => new() { Origin = "Automatic" };
}
