using System.Reflection;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI.Tests;

public class AiInsertFaultProxy : DispatchProxy
{
    internal IMongoCollection<AiAnalysisJob> Target { get; set; } = null!;
    internal Func<AiAnalysisJob, Task> Insert { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IMongoCollection<AiAnalysisJob>.InsertOneAsync) && args?[0] is AiAnalysisJob job)
        {
            return Insert(job);
        }
        return targetMethod!.Invoke(Target, args);
    }
}
