using MongoDB.Driver;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Http.Tests;

internal sealed class TaskLogRetentionFailureMongoContext(IMongoContext inner) : IMongoContext
{
    internal bool FailPolicyAccess { get; set; }
    internal int SuccessfulPolicyAccesses { get; private set; }
    internal int FailedPolicyAccesses { get; private set; }

    /// <inheritdoc />
    public IMongoDatabase Database => inner.Database;

    /// <inheritdoc />
    public IMongoCollection<T> Collection<T>(string name)
    {
        if (name == "taskLogRetentionPolicies")
        {
            if (FailPolicyAccess)
            {
                FailedPolicyAccesses++;
                throw new TimeoutException("The retention policy database became unavailable after the settings revision lookup.");
            }
            SuccessfulPolicyAccesses++;
        }
        return inner.Collection<T>(name);
    }
}
