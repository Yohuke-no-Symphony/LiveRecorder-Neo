using System.Text.RegularExpressions;
using MongoDB.Bson;
using Yohuke.LiveRecorder.Application.Tasks;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Infrastructure.Tasks;

internal static class TaskHistoryProjection
{
    internal const string INTERRUPTED_RECORDING_DISMISSAL_SUFFIX = ":Interrupted";

    internal static List<BsonDocument> Build(TaskHistoryViewer viewer, bool administration, TaskHistoryQuery query, string? taskId = null)
    {
        var pipeline = ById(Media(), taskId);
        pipeline.Add(Union("ai_analysis_jobs", ById(Ai(), taskId)));
        pipeline.Add(Union("transcodeJobs", ById(Transcode(), taskId)));
        if (administration)
        {
            pipeline.Add(Union("recordingSessionTasks", ById(Recording("snapshot"), taskId)));
            pipeline.Add(Union("recordingSessions", ById(Recording("receipt"), taskId)));
            pipeline.Add(Union("sessions", ById(Recording("library"), taskId)));
        }
        if (!administration)
        {
            pipeline.Add(new("$match", new BsonDocument { { "Origin", "User" }, { "InitiatorUserId", viewer.UserId } }));
            if (!viewer.IsAdmin)
            {
                var allowed = new BsonArray { new BsonDocument("Kind", "media") };
                if (viewer.Has(PermissionIds.VIEW_RECORDINGS))
                {
                    allowed.Add(viewer.Has(PermissionIds.VIEW_ANALYTICS)
                        ? new BsonDocument("Kind", "ai")
                        : new BsonDocument { { "Kind", "ai" }, { "Type", AiJobPurposes.LIVE_MARKERS } });
                }
                pipeline.Add(new("$match", new BsonDocument("$or", allowed)));
            }
        }
        pipeline.Add(Lookup("users", "InitiatorUserId", "_creator", new() { { "DisplayName", 1 }, { "AvatarUrl", 1 } }));
        pipeline.Add(Lookup("transcodeWorkers", "WorkerId", "_worker", new("Name", 1)));
        pipeline.Add(new("$set", new BsonDocument
        {
            { "InitiatorName", Cond(Or(Eq("$InitiatorName", ""), Eq("$InitiatorName", BsonNull.Value)),
                First("$_creator.DisplayName"), "$InitiatorName") },
            { "InitiatorAvatarUrl", First("$_creator.AvatarUrl") },
            { "WorkerName", IfNull(First("$_worker.Name"), "$WorkerId") },
            { "FinishedAtUtc", Cond(Terminal(), IfNull("$FinishedAtUtc", "$CreatedAtUtc"), BsonNull.Value) }
        }));
        pipeline.Add(new("$unset", new BsonArray { "_creator", "_worker" }));
        var filter = new BsonDocument();
        Add(filter, "Kind", query.Kind);
        Add(filter, "Type", query.Type);
        Add(filter, "Origin", query.Origin);
        Add(filter, "WorkerId", query.WorkerId);
        Add(filter, "InitiatorUserId", query.InitiatorUserId);
        if (!string.IsNullOrWhiteSpace(query.State) && query.State != "all")
        {
            filter["State"] = query.State switch
            {
                "active" => new BsonDocument("$in", new BsonArray { "Queued", "Running", "Publishing" }),
                "failed" => new BsonDocument("$in", new BsonArray { "Failed", "Interrupted", "Partial" }),
                _ => query.State
            };
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var expression = new BsonRegularExpression(Regex.Escape(query.Search.Trim()), "i");
            filter["$or"] = new BsonArray(new[] { "Title", "Id", "Type", "InitiatorName", "WorkerName" }
                .Select(field => new BsonDocument(field, expression)));
        }
        if (filter.ElementCount > 0)
        {
            pipeline.Add(new("$match", filter));
        }
        pipeline.Add(new("$set", new BsonDocument("DismissalId", Cond(Terminal(), new BsonDocument("$concat", new BsonArray
        {
            administration ? "Admin:" : "User:" + viewer.UserId + ":", "$Kind", ":", "$Id", ":",
            new BsonDocument("$toString", "$Attempt"), ":",
            new BsonDocument("$toString", new BsonDocument("$toLong", "$FinishedAtUtc")),
            Cond(And(Eq("$Kind", "recording"), Eq("$State", "Interrupted")), INTERRUPTED_RECORDING_DISMISSAL_SUFFIX, "")
        }), ""))));
        pipeline.Add(new("$lookup", new BsonDocument
        {
            { "from", "taskHistoryDismissals" }, { "localField", "DismissalId" }, { "foreignField", "_id" }, { "as", "_dismissals" }
        }));
        pipeline.Add(new("$match", new BsonDocument("_dismissals.0", new BsonDocument("$exists", false))));
        pipeline.Add(new("$unset", "_dismissals"));
        return pipeline;
    }

    internal static BsonDocument Sort() => new("$sort", new BsonDocument { { "CreatedAtUtc", -1 }, { "Kind", -1 }, { "Id", -1 } });

    private static List<BsonDocument> ById(List<BsonDocument> pipeline, string? id)
    {
        if (id is not null)
        {
            pipeline.Insert(0, new("$match", new BsonDocument("_id", id)));
        }
        return pipeline;
    }

    private static List<BsonDocument> Media() =>
    [
        new("$lookup", new BsonDocument
        {
            { "from", "clips" }, { "let", new BsonDocument { { "job", "$_id" }, { "attempt", "$Attempt" } } },
            { "pipeline", new BsonArray
            {
                new BsonDocument("$match", new BsonDocument("$expr", And(Eq("$JobId", "$$job"), Eq("$PublicationAttempt", "$$attempt"), Eq("$PublicationPending", true)))),
                new BsonDocument("$limit", 1), new BsonDocument("$project", new BsonDocument("_id", 1))
            } }, { "as", "_pending" }
        }),
        Project("media", "$Kind", "$Title", "$SessionId", UserOrigin(), Initiator(), IfNull("$Provenance.InitiatorName", ""),
            IfNull("$RemoteWorkerId", "local"),
            Cond(And(Eq("$State", "Completed"), new BsonDocument("$gt", new BsonArray { new BsonDocument("$size", "$_pending"), 0 })), "Publishing", "$State"),
            new BsonDocument
            {
                { "Stages", IfNull("$Stages", new BsonArray()) }, { "Stage", IfNull("$Stage", "Processing") },
                { "Mode", "$Mode" }, { "Error", "$Error" }, { "CancelRequested", "$CancelRequested" },
                { "ErrorCode", "$ErrorCode" }, { "ErrorArguments", "$ErrorArguments" },
                { "LastUpdatedAtUtc", "$LastProgressAtUtc" },
                { "OutputCount", new BsonDocument("$size", IfNull("$OutputRelativePaths", new BsonArray())) }
            })
    ];

    private static List<BsonDocument> Ai() =>
    [
        Project("ai", IfNull("$Purpose", AiJobPurposes.ANALYSIS), "$Snapshot.Title", "$SessionId", UserOrigin(), Initiator(),
            IfNull("$Provenance.InitiatorName", IfNull("$OwnerName", "")), IfNull("$WorkerId",
                Cond(And(Eq(IfNull("$Attempt", 0), 0), Eq(IfNull("$Input", BsonNull.Value), BsonNull.Value),
                    new BsonDocument("$ne", new BsonArray { "$State", "Queued" })), "local", BsonNull.Value)),
            Cond(And(Eq("$State", "Running"), Eq("$PublicationStarted", true)), "Publishing", "$State"),
            new BsonDocument
            {
                { "Phase", IfNull("$Context.Phase", "Preparing") }, { "CancelRequested", "$CancelRequested" },
                { "Stages", IfNull("$Stages", new BsonArray()) }, { "LastUpdatedAtUtc", "$LastProgressAtUtc" },
                { "ErrorCode", "$ErrorCode" }, { "PromptTokens", "$PromptTokens" }, { "CompletionTokens", "$CompletionTokens" },
                { "ModelRequests", "$ModelRequests" }, { "ToolCalls", "$ToolCalls" }, { "RequestRetries", "$RequestRetries" },
                { "LiveMarkerFileId", "$LiveMarkerFileId" },
                { "CompletedSegments", new BsonDocument("$size", new BsonDocument("$filter", new BsonDocument
                {
                    { "input", IfNull("$Context.Segments", new BsonArray()) }, { "as", "segment" },
                    { "cond", new BsonDocument("$in", new BsonArray { "$$segment.State", new BsonArray { "Completed", "ReviewedNoTopics", "Empty" } }) }
                })) },
                { "TotalSegments", new BsonDocument("$size", IfNull("$Context.Segments", new BsonArray())) }
            })
    ];

    private static List<BsonDocument> Transcode() =>
    [
        Project("transcode", Literal("Transcode"), "$Title", "$SessionId", IfNull("$Provenance.Origin", "Legacy"),
            IfNull("$Provenance.InitiatorUserId", BsonNull.Value), IfNull("$Provenance.InitiatorName", BsonNull.Value), "$WorkerId",
            Cond(And(Eq("$State", "Completed"), new BsonDocument("$ne", new BsonArray { "$PublicationReady", true })), "Publishing", "$State"),
            new BsonDocument
            {
                { "Stages", IfNull("$Stages", new BsonArray()) }, { "Stage", "$Stage" }, { "Activity", "$Activity" },
                { "ProfileName", "$ProfileName" }, { "Encoder", "$Encoder" }, { "Error", "$Error" }, { "CancelRequested", "$CancelRequested" },
                { "LastUpdatedAtUtc", "$LastProgressAtUtc" }
            })
    ];

    private static List<BsonDocument> Recording(string source)
    {
        var stages = new List<BsonDocument>();
        if (source != "snapshot")
        {
            stages.Add(Lookup("recordingSessionTasks", "_id", "_snapshots"));
            stages.Add(new("$match", new BsonDocument("_snapshots.0", new BsonDocument("$exists", false))));
        }
        if (source == "library")
        {
            stages.Add(new("$match", new BsonDocument("GroupedSessionIds.0", new BsonDocument("$exists", false))));
            stages.Add(Lookup("recordingSessions", "_id", "_receipts"));
            stages.Add(new("$match", new BsonDocument("_receipts.0", new BsonDocument("$exists", false))));
            stages.Add(new("$set", new BsonDocument
            {
                { "Descriptor", new BsonDocument
                {
                    { "SessionId", "$_id" }, { "Title", "$Title" }, { "RoomId", "$RoomId" },
                    { "StartedAtUtc", "$StartedAtUtc" }, { "EndedAtUtc", "$EndedAtUtc" },
                    { "IsClosed", new BsonDocument("$ne", new BsonArray { IfNull("$EndedAtUtc", BsonNull.Value), BsonNull.Value }) }
                } },
                { "_finalized", Cond(Eq("$State", "Completed"), "$EndedAtUtc", BsonNull.Value) },
                { "WorkerId", Cond(Eq(IfNull("$RecordingAssignmentId", BsonNull.Value), BsonNull.Value), "local", BsonNull.Value) },
                { "CommittedArchiveBytes", "$TotalBytes" }, { "AcknowledgedArchiveBytes", "$TotalBytes" },
                { "CapturedDurationMs", new BsonDocument("$subtract", new BsonArray { IfNull("$EndedAtUtc", "$LastMediaAtUtc"), "$StartedAtUtc" }) }
            }));
        }
        else
        {
            stages.Add(new("$set", new BsonDocument("_logicalSessionId", IfNull("$Descriptor.LogicalSessionId", "$_id"))));
            stages.Add(Lookup("sessions", "_logicalSessionId", "_library"));
            if (source == "snapshot")
            {
                stages.Add(Lookup("recordingSessions", "_id", "_receipts"));
                stages.Add(Lookup("recordingTaskInterruptions", "_id", "_interruptions"));
                stages.Add(new("$set", new BsonDocument("_receipt", First("$_receipts"))));
            }
            else
            {
                stages.Add(new("$set", new BsonDocument("_receipt", "$$ROOT")));
            }
            stages.Add(Lookup("recordingAssignments", "AssignmentId", "_assignments"));
            stages.Add(new("$set", new BsonDocument
            {
                { "_finalized", Cond(Eq(new BsonDocument("$type", "$_receipt.FinalizedAtUtc"), "missing"),
                    Cond(Eq(First("$_library.State"), "Completed"), First("$_library.EndedAtUtc"), BsonNull.Value), "$_receipt.FinalizedAtUtc") },
                { "WorkerId", IfNull("$WorkerId", Cond(Eq(First("$_assignments.OwnerKind"), "Worker"), First("$_assignments.OwnerId"), "local")) },
                { "LastCapturedAtUtc", IfNull("$LastCapturedAtUtc", First("$_library.LastMediaAtUtc")) },
                { "CommittedArchiveBytes", IfNull("$CommittedArchiveBytes", IfNull(First("$_library.TotalBytes"), 0)) },
                { "AcknowledgedArchiveBytes", IfNull("$AcknowledgedArchiveBytes", IfNull(First("$_library.TotalBytes"), 0)) },
                { "CaptureState", IfNull("$CaptureState", Cond(Eq("$Descriptor.IsClosed", true), "Closed", "Recording")) }
            }));
        }
        BsonValue interruptedAt = source == "snapshot"
            ? Cond(And(Eq(First("$_interruptions.WorkerId"), "$WorkerId"), Eq(First("$_interruptions.AssignmentId"), "$AssignmentId"),
                    Eq(First("$_interruptions.Generation"), "$Generation"), Eq(First("$_interruptions.LastReportedAtUtc"), "$LastReportedAtUtc")),
                IfNull(First("$_interruptions.CreatedAtUtc"), BsonNull.Value), BsonNull.Value)
            : BsonNull.Value;
        var skipped = Eq("$CaptureState", "NoMedia");
        stages.Add(new("$set", new BsonDocument
        {
            { "CreatedAtUtc", "$Descriptor.StartedAtUtc" }, { "Attempt", 0 },
            { "State", Cond(new BsonDocument("$ne", new BsonArray { IfNull("$_finalized", BsonNull.Value), BsonNull.Value }), "Completed",
                Cond(skipped, "Skipped", Cond(Eq(interruptedAt, BsonNull.Value), "Running", "Interrupted"))) },
            { "FinishedAtUtc", IfNull("$_finalized", Cond(skipped, IfNull("$Descriptor.EndedAtUtc", "$Descriptor.StartedAtUtc"), interruptedAt)) },
            { "Progress", 0 }
        }));
        var currentRecording = And(Eq(First("$_assignments.CurrentSessionId"), "$_id"),
            Eq(First("$_assignments.OwnerKind"), "Worker"), Eq(First("$_assignments.OwnerId"), "$WorkerId"),
            Eq(First("$_assignments.Generation"), "$Generation"),
            new BsonDocument("$in", new BsonArray { IfNull(First("$_assignments.State"), ""), new BsonArray { "Owned", "StopRequested" } }));
        var hasReport = Eq(new BsonDocument("$type", "$LastReportedAtUtc"), "date");
        stages.Add(Project("recording", Literal("Recording"), "$Descriptor.Title", IfNull("$Descriptor.LogicalSessionId", "$_id"), Literal("Automatic"), BsonNull.Value, BsonNull.Value,
            "$WorkerId", "$State", new BsonDocument
            {
                { "CaptureState", IfNull("$CaptureState", Cond(Eq("$Descriptor.IsClosed", true), "Closed", "Recording")) },
                { "CaptureEndedAtUtc", "$Descriptor.EndedAtUtc" },
                { "UploadState", IfNull("$UploadState", Cond(Eq("$State", "Completed"), "Completed", "Queued")) },
                { "CapturedDurationMs", "$CapturedDurationMs" }, { "CommittedArchiveBytes", "$CommittedArchiveBytes" },
                { "AcknowledgedArchiveBytes", "$AcknowledgedArchiveBytes" }, { "LastCapturedAtUtc", "$LastCapturedAtUtc" },
                { "CurrentTransfer", "$CurrentTransfer" }, { "ErrorCode", "$ErrorCode" }, { "ErrorArguments", "$ErrorArguments" },
                { "LastUpdatedAtUtc", IfNull("$LastReportedAtUtc", "$LastCapturedAtUtc") },
                { "InterruptibleSnapshot", source == "snapshot" ? Cond(currentRecording, false, hasReport) : new BsonDocument("$literal", false) },
                { "IsClosed", "$Descriptor.IsClosed" }, { "Playable", IfNull("$_receipt.Published", source == "library") }
            }));
        return stages;
    }

    private static BsonDocument Project(string kind, BsonValue type, BsonValue title, BsonValue session, BsonValue origin,
        BsonValue initiator, BsonValue name, BsonValue worker, BsonValue state, BsonDocument data)
    {
        data["RawState"] = "$State";
        return new("$project", new BsonDocument
        {
        { "_id", 0 }, { "Id", "$_id" }, { "Kind", Literal(kind) }, { "Type", type },
        { "Title", IfNull(title, "") }, { "SessionId", session }, { "Origin", origin },
        { "InitiatorUserId", initiator }, { "InitiatorName", name }, { "WorkerId", IfNull(worker, BsonNull.Value) },
        { "State", IfNull(state, "Queued") }, { "Progress", IfNull("$Progress", 0) }, { "Attempt", IfNull("$Attempt", 0) },
        { "CreatedAtUtc", "$CreatedAtUtc" }, { "FinishedAtUtc", IfNull("$FinishedAtUtc", BsonNull.Value) }, { "Data", data }
        });
    }

    private static BsonDocument Lookup(string collection, string local, string alias, BsonDocument? projection = null)
    {
        return new("$lookup", new BsonDocument
        {
            { "from", collection }, { "localField", local }, { "foreignField", "_id" },
            { "pipeline", projection is null ? new BsonArray() : new BsonArray { new BsonDocument("$project", projection) } }, { "as", alias }
        });
    }

    private static BsonDocument Union(string collection, List<BsonDocument> stages) => new("$unionWith", new BsonDocument
    {
        { "coll", collection }, { "pipeline", new BsonArray(stages) }
    });

    private static BsonDocument UserOrigin() => Cond(new BsonDocument("$in", new BsonArray
    {
        "$Provenance.Origin", new BsonArray { "User", "Automatic" }
    }), "$Provenance.Origin", Cond(new BsonDocument("$ne", new BsonArray { IfNull("$OwnerId", ""), "" }), "User", "Legacy"));

    private static BsonDocument Initiator() => IfNull("$Provenance.InitiatorUserId", IfNull("$OwnerId", BsonNull.Value));
    private static BsonDocument Terminal() => new("$in", new BsonArray { "$State", new BsonArray { "Completed", "Failed", "Cancelled", "Skipped", "Partial", "Interrupted" } });
    private static BsonDocument IfNull(BsonValue first, BsonValue second) => new("$ifNull", new BsonArray { first, second });
    private static BsonDocument Eq(BsonValue first, BsonValue second) => new("$eq", new BsonArray { first, second });
    private static BsonDocument Cond(BsonValue condition, BsonValue yes, BsonValue no) => new("$cond", new BsonArray { condition, yes, no });
    private static BsonDocument And(params BsonValue[] values) => new("$and", new BsonArray(values));
    private static BsonDocument Or(params BsonValue[] values) => new("$or", new BsonArray(values));
    private static BsonDocument First(string path) => new("$arrayElemAt", new BsonArray { path, 0 });
    private static BsonDocument Literal(string value) => new("$literal", value);

    private static void Add(BsonDocument filter, string field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            filter[field] = value;
        }
    }
}
