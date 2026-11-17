using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Infrastructure.Recording;

/// <summary>按持久化归属读取录像节点当前分配的房间及主播资料。</summary>
public sealed class RecordingWorkerAssignmentService(IMongoContext context, RecordingOwnershipService ownership)
{
    /// <summary>返回尚未释放的房间，保留离线或停用节点及停止中的分配。</summary>
    public async Task<IReadOnlyList<RecordingWorkerAssignment>> ListAsync(string workerId, CancellationToken cancellationToken)
    {
        var worker = await context.Collection<TranscodeWorker>("transcodeWorkers")
            .Find(value => value.Id == workerId).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InfrastructureException("Worker 不存在。", 404);
        if (worker.Kind != "Recording")
        {
            throw new InfrastructureException("此节点不接受录像房间。", 409);
        }
        var assignments = await ownership.ListWorkerAsync(workerId, cancellationToken);
        if (assignments.Count == 0)
        {
            return [];
        }
        var targetIds = assignments.Select(value => value.TargetId).Distinct().ToArray();
        var targets = await context.Collection<Streamer>("streamers").Find(value => targetIds.Contains(value.Id))
            .Project(value => new { value.Id, value.Name }).ToListAsync(cancellationToken);
        var names = targets.ToDictionary(value => value.Id, value => value.Name, StringComparer.Ordinal);
        return assignments.Select(value => new RecordingWorkerAssignment
        {
            TargetId = value.TargetId,
            RoomId = value.RoomId,
            Name = names.TryGetValue(value.TargetId, out var name) ? name : value.Definition?.Name ?? ""
        }).ToList();
    }
}
