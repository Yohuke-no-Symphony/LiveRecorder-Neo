using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Application.Localization;
using Yohuke.LiveRecorder.Application.Tasks;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Tasks;

internal static class TaskSummaryMapper
{
    internal static BackgroundTaskSummary From(BsonDocument row, TaskHistoryViewer viewer, WorkerConnectionStatusTracker connections)
    {
        var data = row.GetValue("Data", new BsonDocument()).AsBsonDocument;
        var kind = Text(row, "Kind") ?? "";
        var type = Text(row, "Type") ?? "";
        var state = Text(row, "State") ?? "Queued";
        var worker = Text(row, "WorkerId");
        var active = state is "Queued" or "Running" or "Publishing";
        var terminal = state is "Completed" or "Failed" or "Cancelled" or "Skipped" or "Partial" or "Interrupted";
        var mayOperate = viewer.IsAdmin || Text(row, "InitiatorUserId") == viewer.UserId;
        var permission = kind switch
        {
            "ai" => viewer.Has(PermissionIds.VIEW_RECORDINGS) && (type == AiJobPurposes.LIVE_MARKERS
                ? viewer.Has(PermissionIds.GENERATE_LIVE_MARKERS)
                : viewer.Has(PermissionIds.VIEW_ANALYTICS) && viewer.Has(PermissionIds.GENERATE_ANALYTICS)),
            "media" => type == "Clip" ? viewer.Has(PermissionIds.CREATE_CLIPS) : viewer.IsAdmin,
            "transcode" => viewer.IsAdmin,
            _ => false
        };
        var stages = kind == "recording" ? RecordingStages(row, data) : kind == "ai" ? AiStages(row, data) : WorkerStages(row, data);
        if (terminal)
        {
            stages = stages.Select(stage => stage.State is "Running" or "Waiting"
                ? stage with { State = state is "Partial" or "Interrupted" ? "Interrupted" : state }
                : stage).ToArray();
        }
        return new()
        {
            Id = Text(row, "Id") ?? "", Kind = kind, Type = type, SessionId = Text(row, "SessionId") ?? "",
            Title = Text(row, "Title") ?? "", State = state, Origin = Text(row, "Origin") ?? "Legacy",
            InitiatorUserId = Text(row, "InitiatorUserId"), InitiatorName = Text(row, "InitiatorName"),
            InitiatorAvatarUrl = Text(row, "InitiatorAvatarUrl"), WorkerId = worker, WorkerName = Text(row, "WorkerName"),
            WorkerOnline = worker is null or "local" ? null : connections.IsOnline(worker),
            ProfileName = Text(data, "ProfileName"), Mode = Text(data, "Mode"),
            Progress = state == "Completed" ? 100 : Math.Clamp(Number(row, "Progress") ?? 0, 0, 99.9),
            Attempt = (int)(Number(row, "Attempt") ?? 0), CreatedAtUtc = Date(row, "CreatedAtUtc") ?? DateTime.UnixEpoch,
            FinishedAtUtc = Date(row, "FinishedAtUtc"), LastUpdatedAtUtc = Date(data, "LastUpdatedAtUtc"),
            Error = Text(data, "Error") ?? Describe(Text(data, "ErrorCode")), CancelRequested = Bool(data, "CancelRequested"),
            CanCancel = active && kind != "recording" && mayOperate && permission && !Bool(data, "CancelRequested")
                && Text(data, "RawState") != "Completed"
                && !(kind == "ai" && state == "Publishing"),
            CanRetry = mayOperate && permission && (kind == "transcode" ? state is "Failed" or "Cancelled" or "Skipped"
                : kind == "media" && state is "Failed" or "Cancelled"),
            CanDelete = terminal && mayOperate,
            CanViewResults = kind == "recording" ? Bool(data, "Playable") && viewer.Has(PermissionIds.VIEW_RECORDINGS)
                : viewer.Has(PermissionIds.VIEW_RECORDINGS),
            OutputCount = (int)(Number(data, "OutputCount") ?? 0), Stages = stages
        };
    }

    private static IReadOnlyList<TaskStageSummary> WorkerStages(BsonDocument row, BsonDocument data)
    {
        if (data.TryGetValue("Stages", out var raw) && raw is BsonArray snapshots && snapshots.Count > 0)
        {
            return snapshots.OfType<BsonDocument>().Select(value => BsonSerializer.Deserialize<WorkerStageSnapshot>(value)).Select(stage => new TaskStageSummary
            {
                Id = stage.Id, State = stage.State, Progress = double.IsFinite(stage.Progress) ? Math.Clamp(stage.Progress, 0, 100) : null,
                StartedAtUtc = stage.StartedAtUtc, FinishedAtUtc = stage.FinishedAtUtc, ElapsedMs = stage.ElapsedMs,
                Substate = stage.Substate, Error = stage.Error ?? Describe(stage.ErrorCode),
                Metrics = Metrics(stage.Activity, Text(data, "Encoder"))
            }).ToArray();
        }
        var state = Text(row, "State") ?? "Queued";
        if (state == "Queued")
        {
            return [];
        }
        var activity = data.GetValue("Activity", BsonNull.Value) is BsonDocument document
            ? BsonSerializer.Deserialize<WorkerJobActivity>(document) : null;
        return [new()
        {
            Id = Text(data, "Stage") is { Length: > 0 } id && id is not ("Completed" or "Failed" or "Cancelled" or "WaitingForWorker") ? id : "Processing",
            State = state == "Publishing" ? "Running" : state, Progress = Number(row, "Progress"),
            Error = Text(data, "Error"), Metrics = Metrics(activity, Text(data, "Encoder"))
        }];
    }

    private static IReadOnlyList<TaskStageSummary> AiStages(BsonDocument row, BsonDocument data)
    {
        var state = Text(row, "State") ?? "Queued";
        if (state == "Queued")
        {
            return [];
        }
        var metrics = new List<TaskMetric>();
        Add(metrics, "promptTokens", Number(data, "PromptTokens"));
        Add(metrics, "completionTokens", Number(data, "CompletionTokens"));
        Add(metrics, "modelRequests", Number(data, "ModelRequests"));
        Add(metrics, "toolCalls", Number(data, "ToolCalls"));
        Add(metrics, "completedSegments", Number(data, "CompletedSegments"));
        Add(metrics, "totalSegments", Number(data, "TotalSegments"));
        return [new()
        {
            Id = Text(data, "Phase") ?? "Analyzing", State = state == "Publishing" ? "Running" : state,
            Progress = Number(row, "Progress"), Error = Describe(Text(data, "ErrorCode")), Metrics = metrics
        }];
    }

    private static IReadOnlyList<TaskStageSummary> RecordingStages(BsonDocument row, BsonDocument data)
    {
        var completed = Text(row, "State") == "Completed";
        var skipped = Text(row, "State") == "Skipped";
        var closed = completed || Bool(data, "IsClosed");
        var captured = Number(data, "CommittedArchiveBytes");
        var acknowledged = Number(data, "AcknowledgedArchiveBytes");
        var capture = new List<TaskMetric>();
        Add(capture, "capturedDuration", Number(data, "CapturedDurationMs"), "milliseconds");
        Add(capture, "capturedBytes", captured, "bytes");
        if (Date(data, "LastCapturedAtUtc") is { } last)
        {
            capture.Add(new("lastCaptured", Text: last.ToString("O"), Unit: "date"));
        }
        var upload = new List<TaskMetric>();
        Add(upload, "acknowledgedBytes", acknowledged, "bytes");
        Add(upload, "capturedBytes", captured, "bytes");
        if (data.GetValue("CurrentTransfer", BsonNull.Value) is BsonDocument transfer)
        {
            Add(upload, "bytesTransferred", Number(transfer, "BytesTransferred"), "bytes");
            Add(upload, "totalBytes", Number(transfer, "TotalBytes"), "bytes");
            if (Text(transfer, "ArtifactKind") is { } artifact)
            {
                upload.Add(new("currentFile", Text: artifact));
            }
        }
        var uploadState = Text(data, "UploadState");
        return
        [
            new()
            {
                Id = "Capturing", State = skipped ? "Skipped" : closed ? "Completed" : "Running",
                Progress = closed ? 100 : null, Substate = Text(data, "CaptureState"),
                StartedAtUtc = Date(row, "CreatedAtUtc"), FinishedAtUtc = closed ? Date(data, "CaptureEndedAtUtc") ?? Date(row, "FinishedAtUtc") : null,
                Error = closed ? null : Describe(Text(data, "ErrorCode")), Metrics = capture
            },
            new()
            {
                Id = "Uploading", State = completed ? "Completed" : skipped ? "Skipped" : "Running",
                Progress = completed ? 100 : captured is > 0 && acknowledged is { } count ? Math.Clamp(count / captured.Value * 100, 0, 100) : null,
                Substate = completed ? "Completed" : uploadState,
                FinishedAtUtc = completed ? Date(row, "FinishedAtUtc") : null,
                Error = uploadState == "Retrying" ? Describe(Text(data, "ErrorCode")) : null, Metrics = upload
            }
        ];
    }

    private static IReadOnlyList<TaskMetric> Metrics(WorkerJobActivity? activity, string? encoder)
    {
        var metrics = new List<TaskMetric>();
        if (activity is not null)
        {
            Add(metrics, "bytesTransferred", activity.BytesTransferred, "bytes");
            Add(metrics, "totalBytes", activity.TotalBytes, "bytes");
            Add(metrics, "transferRate", activity.BytesPerSecond, "bytesPerSecond");
            Add(metrics, "processedDuration", activity.ProcessedDurationMs, "milliseconds");
            Add(metrics, "totalDuration", activity.TotalDurationMs, "milliseconds");
            Add(metrics, "framesPerSecond", activity.FramesPerSecond, "fps");
            Add(metrics, "speed", activity.Speed, "speed");
            Add(metrics, "estimatedRemaining", activity.EstimatedSecondsRemaining * 1000, "milliseconds");
            Add(metrics, "filesCompleted", activity.FilesCompleted);
            Add(metrics, "filesTotal", activity.FilesTotal);
            if (!string.IsNullOrWhiteSpace(activity.CurrentFile))
            {
                metrics.Add(new("currentFile", Text: activity.CurrentFile));
            }
        }
        if (!string.IsNullOrWhiteSpace(encoder))
        {
            metrics.Add(new("encoder", Text: encoder));
        }
        return metrics;
    }

    private static void Add(List<TaskMetric> metrics, string key, double? value, string unit = "number")
    {
        if (value is { } number && double.IsFinite(number) && number >= 0)
        {
            metrics.Add(new(key, number, unit));
        }
    }

    private static string? Describe(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }
        if (code is "RecordingUploadFailed" or "RecordingSourceUnavailable" or "RecordingStorageFull")
        {
            return LocalizedText.Get(code switch
            {
                "RecordingUploadFailed" => MessageCode.RECORDING_UPLOAD_FAILED,
                "RecordingSourceUnavailable" => MessageCode.RECORDING_SOURCE_UNAVAILABLE,
                _ => MessageCode.RECORDING_DISK_SPACE_LOW
            });
        }
        return code.StartsWith("AI_", StringComparison.Ordinal) ? AiErrors.Describe(code)
            : Enum.TryParse<MessageCode>(code, out var message) && Enum.IsDefined(message) ? LocalizedText.Get(message) : code;
    }
    private static string? Text(BsonDocument value, string field) => value.TryGetValue(field, out var raw) && !raw.IsBsonNull ? raw.AsString : null;
    private static double? Number(BsonDocument value, string field) => value.TryGetValue(field, out var raw) && raw.IsNumeric ? raw.ToDouble() : null;
    private static DateTime? Date(BsonDocument value, string field) => value.TryGetValue(field, out var raw) && raw.IsValidDateTime ? raw.ToUniversalTime() : null;
    private static bool Bool(BsonDocument value, string field) => value.TryGetValue(field, out var raw) && raw is BsonBoolean boolean && boolean.Value;
}
