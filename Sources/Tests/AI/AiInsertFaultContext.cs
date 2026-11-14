using System.Reflection;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.AI.Tests;

internal sealed class AiInsertFaultContext(IMongoContext context, bool commitBeforeFailure) : IMongoContext
{
    public IMongoDatabase Database => context.Database;

    public IMongoCollection<T> Collection<T>(string name)
    {
        if (typeof(T) != typeof(AiAnalysisJob))
        {
            return context.Collection<T>(name);
        }
        var actual = context.Collection<AiAnalysisJob>(name);
        var proxy = DispatchProxy.Create<IMongoCollection<AiAnalysisJob>, AiInsertFaultProxy>();
        var configuration = (AiInsertFaultProxy)(object)proxy;
        configuration.Target = actual;
        configuration.Insert = async job =>
        {
            if (commitBeforeFailure)
            {
                await actual.InsertOneAsync(job);
            }
            throw new TimeoutException("Injected uncertain insert outcome.");
        };
        return (IMongoCollection<T>)(object)proxy;
    }
}
