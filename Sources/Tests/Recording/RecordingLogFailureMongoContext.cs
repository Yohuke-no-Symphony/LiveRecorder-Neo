using MongoDB.Driver;
using Yohuke.LiveRecorder.Infrastructure.Persistence;

namespace Yohuke.LiveRecorder.Recording.Tests;

internal sealed class RecordingLogFailureMongoContext(Exception failure) : IMongoContext
{
    public IMongoDatabase Database => throw failure;

    public IMongoCollection<T> Collection<T>(string name) => throw failure;
}
