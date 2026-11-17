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
        var error = Error(data);
        if (!viewer.IsAdmin)
        {
            var publicError = LocalizedText.Get(MessageCode.TASK_EXECUTION_FAILED);
            if (error is not null)
            {
                error = Describe(Text(data, "ErrorCode"), fallback: publicError) ?? publicError;
            }
            stages = stages.Select(stage => stage with
            {
                Error = stage.Error is null ? null : error ?? publicError,
                Metrics = stage.Metrics.Select(metric => metric.Key == "currentFile" && metric.Text is { } path
                    ? metric with { Text = Path.GetFileName(path.Replace('\\', '/')) }
                    : metric).ToArray()
            }).ToArray();
        }
        return new()
        {
            Id = Text(row, "Id") ?? "", Kind = kind, Type = type, SessionId = Text(row, "SessionId") ?? "",
            Title = Text(row, "Title") ?? "", State = state, Origin = Text(row, "Origin") ?? "Legacy",
            InitiatorUserId = Text(row, "InitiatorUserId"), InitiatorName = Text(row, "InitiatorName"),
            InitiatorAvatarUrl = Text(row, "InitiatorAvatarUrl"), WorkerId = worker, WorkerName = Text(row, "WorkerName"),
            WorkerOnline = worker is null or "local" ? null : connections.IsOnline(worker),
            ProfileName = Text(data, "ProfileName"), Mode = Text(data, "Mode"),
            LiveMarkerFileId = Text(data, "LiveMarkerFileId"),
            Progress = state == "Completed" ? 100 : Math.Clamp(Number(row, "Progress") ?? 0, 0, 99.9),
            Attempt = (int)(Number(row, "Attempt") ?? 0), CreatedAtUtc = Date(row, "CreatedAtUtc") ?? DateTime.UnixEpoch,
            FinishedAtUtc = Date(row, "FinishedAtUtc"), LastUpdatedAtUtc = Date(data, "LastUpdatedAtUtc"),
            Error = error, CancelRequested = Bool(data, "CancelRequested"),
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
        var state = Text(row, "State") ?? "Queued";
        if (data.TryGetValue("Stages", out var raw) && raw is BsonArray snapshots && snapshots.Count > 0
            && (state == "Queued" || snapshots.OfType<BsonDocument>().Any(stage => Text(stage, "State") is not (null or "Pending"))))
        {
            return snapshots.OfType<BsonDocument>().Select(value => BsonSerializer.Deserialize<WorkerStageSnapshot>(value)).Select(stage => new TaskStageSummary
            {
                Id = stage.Id, State = stage.State,
                Progress = stage.State == "Running" && stage.Progress == 0 && stage.Activity is null ? null
                    : double.IsFinite(stage.Progress) ? Math.Clamp(stage.Progress, 0, 100) : null,
                StartedAtUtc = stage.StartedAtUtc, FinishedAtUtc = stage.FinishedAtUtc, ElapsedMs = stage.ElapsedMs,
                Substate = stage.Substate, Error = Describe(stage.ErrorCode, fallback: stage.Error),
                Metrics = Metrics(stage.Activity, Text(data, "Encoder"))
            }).ToArray();
        }
        if (state == "Queued")
        {
            return [];
        }
        var activity = data.GetValue("Activity", BsonNull.Value) is BsonDocument document
            ? BsonSerializer.Deserialize<WorkerJobActivity>(document) : null;
        return [new()
        {
            Id = Text(data, "Stage") is { Length: > 0 } id && id is not ("Completed" or "Failed" or "Cancelled" or "WaitingForWorker") ? id : "Processing",
            State = state == "Publishing" ? "Running" : state,
            Progress = state == "Completed" ? 100 : ActivityProgress(activity),
            Error = Error(data), Metrics = Metrics(activity, Text(data, "Encoder"))
        }];
    }

    private static double? ActivityProgress(WorkerJobActivity? activity)
    {
        if (activity?.TotalBytes is > 0 && activity.BytesTransferred is { } bytes)
        {
            return Math.Clamp(bytes / (double)activity.TotalBytes.Value * 100, 0, 100);
        }
        return activity?.TotalDurationMs is > 0 && activity.ProcessedDurationMs is { } duration
            ? Math.Clamp(duration / activity.TotalDurationMs.Value * 100, 0, 100) : null;
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

    private static string? Error(BsonDocument data)
    {
        var arguments = data.GetValue("ErrorArguments", BsonNull.Value) is BsonArray values
            ? values.Where(value => value.IsString).Select(value => value.AsString).ToArray() : [];
        return Describe(Text(data, "ErrorCode"), arguments, Text(data, "Error"));
    }

    private static string? Describe(string? code, IReadOnlyList<string>? arguments = null, string? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return fallback;
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
        try
        {
            return code.StartsWith("AI_", StringComparison.Ordinal) ? AiErrors.Describe(code)
                : LocalizedText.Format(code, fallback ?? code, arguments);
        }
        catch (FormatException)
        {
            return fallback ?? code;
        }
    }
    private static string? Text(BsonDocument value, string field) => value.TryGetValue(field, out var raw) && !raw.IsBsonNull ? raw.AsString : null;
    private static double? Number(BsonDocument value, string field) => value.TryGetValue(field, out var raw) && raw.IsNumeric ? raw.ToDouble() : null;
    private static DateTime? Date(BsonDocument value, string field) => value.TryGetValue(field, out var raw) && raw.IsValidDateTime ? raw.ToUniversalTime() : null;
    private static bool Bool(BsonDocument value, string field) => value.TryGetValue(field, out var raw) && raw is BsonBoolean boolean && boolean.Value;
}
