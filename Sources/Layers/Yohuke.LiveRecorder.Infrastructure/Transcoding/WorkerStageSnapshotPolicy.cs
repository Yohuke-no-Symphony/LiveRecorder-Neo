using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

/// <summary>约束有限任务上报的阶段身份、顺序和诊断内容。</summary>
public static class WorkerStageSnapshotPolicy
{
    /// <summary>建立指定流水线的有序初始阶段。</summary>
    public static List<WorkerStageSnapshot> Create(string kind) => Ids(kind).Select(id => new WorkerStageSnapshot { Id = id }).ToList();

    /// <summary>接收有界且身份唯一的扩展阶段并保留上报顺序，旧节点的空快照保持兼容。</summary>
    public static List<WorkerStageSnapshot> Normalize(string kind, IReadOnlyList<WorkerStageSnapshot>? stages)
    {
        if (stages is null || stages.Count is < 1 or > 32 || stages.Any(stage => stage is null || !ValidId(stage.Id))
            || stages.Select(stage => stage.Id).Distinct(StringComparer.Ordinal).Count() != stages.Count)
        {
            return [];
        }
        return stages.Select(stage => stage with
        {
            State = stage.State is "Pending" or "Running" or "Waiting" or "Completed" or "Failed" or "Cancelled" or "Skipped" or "Interrupted"
                ? stage.State : "Pending",
            Progress = double.IsFinite(stage.Progress) ? Math.Clamp(stage.Progress, 0, 100) : 0,
            ElapsedMs = double.IsFinite(stage.ElapsedMs) ? Math.Max(0, stage.ElapsedMs) : 0,
            Substate = Limit(stage.Substate, 128), ErrorCode = Limit(stage.ErrorCode, 128), Error = Limit(stage.Error, 2048),
            Activity = TranscodeJobProgressPolicy.NormalizeActivity(LegacyStage(stage.Id), stage.Activity)
        }).ToList();
    }

    /// <summary>标记服务端已经确认发布的最终阶段。</summary>
    public static List<WorkerStageSnapshot> Complete(string kind, IReadOnlyList<WorkerStageSnapshot>? stages)
    {
        var result = Normalize(kind, stages);
        if (result.Count == 0)
        {
            result = Create(kind);
        }
        var now = DateTime.UtcNow;
        return result.Select(stage => stage with { State = "Completed", Progress = 100, FinishedAtUtc = stage.FinishedAtUtc ?? now }).ToList();
    }

    private static string[] Ids(string kind) => kind == "Clipping"
        ? ["Downloading", "Preparing", "Encoding", "Validating", "Uploading", "Finalizing"]
        : ["Downloading", "Inspecting", "Encoding", "Validating", "Uploading", "Finalizing"];

    private static bool ValidId(string? id) => id is { Length: > 0 and <= 64 }
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string LegacyStage(string id) => id switch
    {
        "Download" => "Downloading", "Upload" => "Uploading", "Encode" or "Export" => "Encoding", _ => id
    };

    private static string? Limit(string? value, int length) => value?.Length > length ? value[..length] : value;
}
