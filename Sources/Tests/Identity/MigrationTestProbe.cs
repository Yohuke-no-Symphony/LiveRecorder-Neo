using System.Collections.Concurrent;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Infrastructure.Configuration;

namespace Yohuke.LiveRecorder.Identity.Tests;

internal sealed class MigrationTestProbe
{
    internal const string CHECKPOINT = "completed-first-copy";

    internal ConcurrentQueue<string> Phases { get; } = new();
    internal bool FailVerification { get; init; }
    internal bool PauseMigration { get; init; }
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal string? FirstCheckpoint { get; set; }
    internal string? SecondCheckpoint { get; set; }
    internal string? FirstSourceVersion { get; set; }
    internal IMongoDatabase? FirstDatabase { get; set; }
    internal IAppPaths? FirstPaths { get; set; }
    internal bool WroteCheckpoint { get; set; }
}
