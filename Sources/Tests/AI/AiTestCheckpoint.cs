using System.Text.Json;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI.Tests;

internal sealed class AiTestCheckpoint : IAiRunCheckpoint
{
    internal List<AiAnalysisJob> Saved { get; } = [];
    internal List<AiAnalysisMemory> Memories { get; } = [];

    public Task SaveAsync(AiAnalysisJob job, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Saved.Add(JsonSerializer.Deserialize<AiAnalysisJob>(JsonSerializer.Serialize(job))!);
        return Task.CompletedTask;
    }

    public Task SaveMemoryAsync(AiAnalysisJob job, AiAnalysisMemory memory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Memories.Add(JsonSerializer.Deserialize<AiAnalysisMemory>(JsonSerializer.Serialize(memory))!);
        return SaveAsync(job, cancellationToken);
    }

    public Task<AiAnalysisMemory> LoadMemoryAsync(AiAnalysisJob job, CancellationToken cancellationToken) =>
        Task.FromResult(Memories.LastOrDefault() is { } memory
            ? JsonSerializer.Deserialize<AiAnalysisMemory>(JsonSerializer.Serialize(memory))! : new AiAnalysisMemory());
}
