namespace Yohuke.LiveRecorder.Application.Tasks;

/// <summary>对全部任务来源使用相同的筛选与游标分页规则。</summary>
public sealed record TaskHistoryQuery
{
    public string? State { get; init; }
    public string? Kind { get; init; }
    public string? Type { get; init; }
    public string? Origin { get; init; }
    public string? WorkerId { get; init; }
    public string? InitiatorUserId { get; init; }
    public string? Search { get; init; }
    public string? Cursor { get; init; }
    public int Limit { get; init; } = 50;
}
