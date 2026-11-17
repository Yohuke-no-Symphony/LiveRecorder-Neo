using System.Security.Cryptography;
using System.Text.Json;
using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Transcoding;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.AI;

/// <summary>将交互式模型连通性检查交给有容量的 AI 节点，密钥只保留在请求生命周期。</summary>
public sealed class AiConnectionTestCoordinator
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, AiConnectionProbe> probes = new(StringComparer.Ordinal);

    internal void Maintain()
    {
        lock (gate)
        {
            Expire();
        }
    }

    internal int ActiveCount
    {
        get
        {
            lock (gate)
            {
                Expire();
                return probes.Values.Count(value => !value.Completion.Task.IsCompleted);
            }
        }
    }

    internal int RunningCount(string? workerId = null)
    {
        lock (gate)
        {
            Expire();
            return probes.Values.Count(value => value.WorkerId is not null && !value.Completion.Task.IsCompleted
                && (workerId is null || value.WorkerId == workerId));
        }
    }

    internal AiConnectionProbe Enqueue(AiSettings settings, string apiKey)
    {
        lock (gate)
        {
            var probe = new AiConnectionProbe { Settings = settings, ApiKey = apiKey };
            probes.Add(probe.Id, probe);
            return probe;
        }
    }

    internal AiAssignment? Assign(string workerId)
    {
        lock (gate)
        {
            Expire();
            var probe = probes.Values.FirstOrDefault(value => value.WorkerId is null && !value.Completion.Task.IsCompleted);
            if (probe is null)
            {
                return null;
            }
            probe.WorkerId = workerId;
            probe.LeaseToken = TranscodeWorkerService.NewSecret();
            probe.LeaseUntilUtc = DateTime.UtcNow.AddSeconds(90);
            return probe.Assignment;
        }
    }

    internal bool Contains(string id)
    {
        lock (gate)
        {
            return probes.ContainsKey(id);
        }
    }

    internal bool Renew(string workerId, string id, int attempt, string token)
    {
        lock (gate)
        {
            Expire();
            if (!Matches(workerId, id, attempt, token, out var probe) || probe.Completion.Task.IsCompleted)
            {
                return false;
            }
            probe.LeaseUntilUtc = DateTime.UtcNow.AddSeconds(90);
            return true;
        }
    }

    internal AiExecutionSpecification Specification(string workerId, string id, int attempt, string token)
    {
        lock (gate)
        {
            if (!Matches(workerId, id, attempt, token, out var probe) || probe.Completion.Task.IsCompleted)
            {
                throw AiErrors.CreateException("AI_LEASE_LOST", 409);
            }
            return new()
            {
                IsConnectionTest = true, ApiKey = probe.ApiKey, CheckpointSequence = probe.Sequence,
                Execution = new() { JobId = id, Settings = AiExecutionMapper.ToSettings(probe.Settings) }
            };
        }
    }

    internal AiCheckpointAcknowledgement Checkpoint(string workerId, string id, int attempt, string token, AiExecutionCheckpoint checkpoint)
    {
        lock (gate)
        {
            if (!Matches(workerId, id, attempt, token, out var probe) || probe.Completion.Task.IsCompleted)
            {
                throw AiErrors.CreateException("AI_LEASE_LOST", 409);
            }
            var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(checkpoint, WorkerProtocol.JsonOptions)));
            if (checkpoint.Execution.JobId != id || checkpoint.Sequence != probe.Sequence + 1
                && (checkpoint.Sequence != probe.Sequence || probe.CheckpointHash != hash))
            {
                throw AiErrors.CreateException("AI_JOB_CONFLICT", 409);
            }
            probe.Sequence = checkpoint.Sequence;
            probe.CheckpointHash = hash;
            return new() { Sequence = probe.Sequence, State = "Running" };
        }
    }

    internal AiCheckpointAcknowledgement Complete(string workerId, string id, int attempt, string token, AiExecutionResult result)
    {
        lock (gate)
        {
            if (!Matches(workerId, id, attempt, token, out var probe))
            {
                throw AiErrors.CreateException("AI_LEASE_LOST", 409);
            }
            var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(result, WorkerProtocol.JsonOptions)));
            if (probe.CompletionHash is not null)
            {
                if (probe.CompletionHash != hash || probe.Sequence != result.Sequence)
                {
                    throw AiErrors.CreateException("AI_JOB_CONFLICT", 409);
                }
                return new() { Sequence = probe.Sequence, State = "Completed" };
            }
            if (result.Sequence != probe.Sequence + 1 || result.Execution.JobId != id)
            {
                throw AiErrors.CreateException("AI_JOB_CONFLICT", 409);
            }
            probe.Sequence = result.Sequence;
            probe.CompletionHash = hash;
            probe.ApiKey = "";
            if (result.ConnectionTest is { Success: true } outcome)
            {
                probe.Completion.TrySetResult(new(true, outcome.PromptTokens, outcome.CompletionTokens));
            }
            else
            {
                probe.Completion.TrySetException(AiErrors.CreateException(result.Execution.ErrorCode ?? "AI_REQUEST_FAILED", 502));
            }
            return new() { Sequence = result.Sequence, State = "Completed" };
        }
    }

    internal void Release(AiConnectionProbe probe)
    {
        lock (gate)
        {
            probe.ApiKey = "";
            probe.Completion.TrySetCanceled();
        }
    }

    internal static string ConfigurationFingerprint(AiSettings settings) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(AiExecutionMapper.ToSettings(settings), WorkerProtocol.JsonOptions)));

    private bool Matches(string workerId, string id, int attempt, string token, out AiConnectionProbe probe)
    {
        if (probes.TryGetValue(id, out var value) && value.WorkerId == workerId && attempt == 1
            && value.LeaseUntilUtc > DateTime.UtcNow && value.LeaseToken == token)
        {
            probe = value;
            return true;
        }
        probe = null!;
        return false;
    }

    private void Expire()
    {
        var now = DateTime.UtcNow;
        foreach (var probe in probes.Values.Where(value => !value.Completion.Task.IsCompleted
            && (value.WorkerId is null ? value.CreatedAtUtc.AddSeconds(30) <= now : value.LeaseUntilUtc <= now)))
        {
            probe.ApiKey = "";
            probe.Completion.TrySetException(AiErrors.CreateException("AI_WORKER_UNAVAILABLE", 503));
        }
        foreach (var id in probes.Where(pair => pair.Value.Completion.Task.IsCompleted
            && pair.Value.CreatedAtUtc < now.AddMinutes(-15)).Select(pair => pair.Key).ToArray())
        {
            probes.Remove(id);
        }
    }
}
