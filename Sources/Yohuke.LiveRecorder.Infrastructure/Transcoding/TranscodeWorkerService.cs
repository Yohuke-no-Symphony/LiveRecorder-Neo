using System.Security.Cryptography;
using System.Text;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Domain;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Infrastructure.Transcoding;

/// <summary>管理远端节点凭据和带版本的运行配置。</summary>
public sealed class TranscodeWorkerService(IMongoContext context, IAppPaths paths)
{
    /// <summary>读取节点管理视图。</summary>
    public async Task<IReadOnlyList<TranscodeWorker>> ListAsync(CancellationToken cancellationToken) =>
        await Workers.Find(FilterDefinition<TranscodeWorker>.Empty).SortBy(x => x.CreatedAtUtc).ToListAsync(cancellationToken);

    /// <summary>创建节点并仅在创建响应中返回随机凭据。</summary>
    public async Task<(TranscodeWorker Worker, string Secret)> CreateAsync(string name, CancellationToken cancellationToken)
    {
        return await CreateAsync(name, "Transcoding", cancellationToken);
    }

    /// <summary>Creates an independently authenticated Worker for the requested task kind.</summary>
    public async Task<(TranscodeWorker Worker, string Secret)> CreateAsync(string name, string kind, CancellationToken cancellationToken)
    {
        ValidateName(name);
        if (kind is not ("Transcoding" or "Clipping"))
        {
            throw new InfrastructureException("Worker 类型无效。");
        }
        var secret = NewSecret();
        var worker = new TranscodeWorker { Name = name.Trim(), Kind = kind, SecretHash = SecretHash(secret) };
        await Workers.InsertOneAsync(worker, cancellationToken: cancellationToken);
        return (worker, secret);
    }

    /// <summary>按配置版本保存后台变更，停用节点立即撤销其租约。</summary>
    public async Task<TranscodeWorker> UpdateAsync(string id, string name, bool enabled, WorkerConfiguration configuration,
        int expectedRevision, CancellationToken cancellationToken)
    {
        await using var identityLock = await WorkerIdentityLock.AcquireAsync(paths, id, cancellationToken);
        ValidateName(name);
        var current = await Workers.Find(x => x.Id == id).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InfrastructureException("Worker 不存在。", 404);
        WorkerConfigurationPolicy.Validate(configuration, current.Kind);
        var worker = await Workers.FindOneAndUpdateAsync(x => x.Id == id && x.ConfigurationRevision == expectedRevision,
            Builders<TranscodeWorker>.Update.Set(x => x.Name, name.Trim()).Set(x => x.Enabled, enabled)
                .Set(x => x.Configuration, configuration).Inc(x => x.ConfigurationRevision, 1),
            new FindOneAndUpdateOptions<TranscodeWorker> { ReturnDocument = ReturnDocument.After }, cancellationToken)
            ?? throw new InfrastructureException("Worker 配置已被修改，请刷新后重试。", 409);
        if (!enabled)
        {
            await ReleaseJobsAsync(id, cancellationToken);
        }
        return worker;
    }

    /// <summary>轮换凭据，同时撤销旧连接取得的执行权。</summary>
    public async Task<string> RotateAsync(string id, CancellationToken cancellationToken)
    {
        await using var identityLock = await WorkerIdentityLock.AcquireAsync(paths, id, cancellationToken);
        var secret = NewSecret();
        var changed = await Workers.UpdateOneAsync(x => x.Id == id,
            Builders<TranscodeWorker>.Update.Set(x => x.SecretHash, SecretHash(secret)), cancellationToken: cancellationToken);
        if (changed.MatchedCount == 0)
        {
            throw new InfrastructureException("Worker 不存在。", 404);
        }
        await ReleaseJobsAsync(id, cancellationToken);
        return secret;
    }

    /// <summary>删除节点登记，将其未完成任务交还队列。</summary>
    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await using var identityLock = await WorkerIdentityLock.AcquireAsync(paths, id, cancellationToken);
        await Workers.DeleteOneAsync(x => x.Id == id, cancellationToken);
        await ReleaseJobsAsync(id, cancellationToken);
    }

    /// <summary>使用恒定时间比较验证随机 Worker 凭据。</summary>
    public async Task<bool> AuthenticateAsync(string id, string secret, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 256)
        {
            return false;
        }
        var worker = await Workers.Find(x => x.Id == id && x.Enabled).FirstOrDefaultAsync(cancellationToken);
        return worker is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(worker.SecretHash), Encoding.ASCII.GetBytes(SecretHash(secret)));
    }

    /// <summary>连接时刷新能力，并按版本接收终端提交的配置。</summary>
    public async Task<WorkerWelcome> ConnectAsync(WorkerHello hello, CancellationToken cancellationToken)
    {
        await using var identityLock = await WorkerIdentityLock.AcquireAsync(paths, hello.WorkerId, cancellationToken);
        if (!await AuthenticateAsync(hello.WorkerId, hello.Secret, cancellationToken))
        {
            throw new InfrastructureException("Worker 凭据无效或节点已停用。", 401);
        }
        var registered = await RequireAsync(hello.WorkerId, cancellationToken);
        if (hello.Kind != registered.Kind)
        {
            throw new InfrastructureException("Worker 类型与登记类型不符。", 409);
        }
        var filter = Builders<TranscodeWorker>.Filter.Where(x => x.Id == hello.WorkerId && x.Enabled);
        var update = Builders<TranscodeWorker>.Update.Set(x => x.LastSeenAtUtc, DateTime.UtcNow)
            .Set(x => x.Capabilities, hello.Capabilities);
        if (hello.InitialConfiguration is not null)
        {
            WorkerConfigurationPolicy.Validate(hello.InitialConfiguration, registered.Kind);
            if (hello.ExpectedConfigurationRevision is null)
            {
                throw new InfrastructureException("提交 Worker 配置时必须携带当前版本。", 409);
            }
            filter &= Builders<TranscodeWorker>.Filter.Eq(x => x.ConfigurationRevision, hello.ExpectedConfigurationRevision.Value);
            update = update.Set(x => x.Configuration, hello.InitialConfiguration).Inc(x => x.ConfigurationRevision, 1);
        }
        var worker = await Workers.FindOneAndUpdateAsync(filter, update,
            new FindOneAndUpdateOptions<TranscodeWorker> { ReturnDocument = ReturnDocument.After }, cancellationToken)
            ?? throw new InfrastructureException("Worker 配置已被修改，请重新连接。", 409);
        return new() { Configuration = worker.Configuration, ConfigurationRevision = worker.ConfigurationRevision };
    }

    /// <summary>读取仍被允许执行的节点。</summary>
    public async Task<TranscodeWorker> RequireAsync(string id, CancellationToken cancellationToken) =>
        await Workers.Find(x => x.Id == id && x.Enabled).FirstOrDefaultAsync(cancellationToken)
        ?? throw new InfrastructureException("Worker 已停用或不存在。", 401);

    private IMongoCollection<TranscodeWorker> Workers => context.Collection<TranscodeWorker>("transcodeWorkers");

    private async Task ReleaseJobsAsync(string id, CancellationToken cancellationToken)
    {
        await context.Collection<TranscodeJob>("transcodeJobs").UpdateManyAsync(
            x => x.WorkerId == id && (x.State == "Running" || x.State == "Publishing"),
            Builders<TranscodeJob>.Update.Set(x => x.State, "Queued").Set(x => x.Stage, "WaitingForWorker")
                .Set(x => x.WorkerId, null).Set(x => x.LeaseTokenHash, null).Set(x => x.LeaseUntilUtc, null),
            cancellationToken: cancellationToken);
        var jobs = context.Collection<MediaJob>("jobs");
        var active = await jobs.Find(x => x.RemoteWorkerId == id && (x.State == "Running" || x.State == "Publishing"))
            .ToListAsync(cancellationToken);
        foreach (var job in active)
        {
            await using var executionLock = await MediaJobExecutionLock.AcquireAsync(paths, job.Id, cancellationToken);
            await jobs.UpdateOneAsync(x => x.Id == job.Id && x.RemoteWorkerId == id && (x.State == "Running" || x.State == "Publishing"),
                Builders<MediaJob>.Update.Set(x => x.State, "Queued").Set(x => x.RemoteWorkerId, null)
                    .Set(x => x.RemoteLeaseTokenHash, null).Set(x => x.LeaseOwner, null).Set(x => x.LeaseUntilUtc, null),
                cancellationToken: cancellationToken);
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
        {
            throw new InfrastructureException("Worker 名称长度必须为 1 至 100 个字符。");
        }
    }

    internal static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    internal static string SecretHash(string value) => TranscodeAssetRegistry.Hash(value);

    internal static void RequireCredential(TranscodeWorker worker, string? expectedHash)
    {
        if (expectedHash is not null && !CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(worker.SecretHash), Encoding.ASCII.GetBytes(expectedHash)))
        {
            throw new InfrastructureException("Worker 连接凭据已被撤销。", 401);
        }
    }
}
