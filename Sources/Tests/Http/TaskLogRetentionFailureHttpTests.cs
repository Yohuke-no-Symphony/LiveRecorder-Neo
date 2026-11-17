using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Infrastructure.Configuration;
using Yohuke.LiveRecorder.Infrastructure.Jobs;
using Yohuke.LiveRecorder.Infrastructure.Persistence;
using Yohuke.LiveRecorder.Infrastructure.Recording;
using Yohuke.LiveRecorder.Infrastructure.Tasks;
using Xunit;

namespace Yohuke.LiveRecorder.Http.Tests;

/// <summary>验证真实配置保存失败和落盘后策略发布失败的恢复边界。</summary>
public sealed class TaskLogRetentionFailureHttpTests
{
    /// <summary>原子替换配置文件失败时，内存设置、共享策略版本与实际TTL均保持原值。</summary>
    [MongoHttpFact]
    public async Task FailedSettingsFileReplacementDoesNotPublishRetention()
    {
        await using var fixture = await FixtureAsync();
        var settings = fixture.App.Services.GetRequiredService<ISettingsStore>();
        using var policy = new TaskLogRetentionPolicy(fixture.Context, settings);
        await policy.InitializeAsync();
        var originalRevision = await policy.RevisionAsync();
        var original = await File.ReadAllTextAsync(fixture.ConfigFile);
        var backup = fixture.ConfigFile + ".saved";
        var failing = new TaskLogRetentionFailureSettingsStore(settings, () =>
        {
            File.Move(fixture.ConfigFile, backup);
            Directory.CreateDirectory(fixture.ConfigFile);
        });
        var service = new TaskLogRetentionSettingsService(failing, policy);
        try
        {
            await Assert.ThrowsAnyAsync<IOException>(() => service.UpdateAsync(value =>
            {
                value.TaskLogRetentionDays = 0;
                return value;
            }));
        }
        finally
        {
            if (Directory.Exists(fixture.ConfigFile))
            {
                Directory.Delete(fixture.ConfigFile);
            }
            if (File.Exists(backup))
            {
                File.Move(backup, fixture.ConfigFile);
            }
        }

        Assert.Equal(1, failing.Updates);
        Assert.Equal(original, await File.ReadAllTextAsync(fixture.ConfigFile));
        Assert.Empty(Directory.EnumerateFiles(fixture.DirectoryPath, "*.tmp"));
        var saved = await settings.GetAsync();
        Assert.Equal(7, saved.TaskLogRetentionDays);
        Assert.Null(saved.TaskLogRetentionPending);
        var reloaded = new StartupConfigurationStore(fixture.ConfigFile).ReadSettings();
        Assert.Equal(7, reloaded.TaskLogRetentionDays);
        Assert.Null(reloaded.TaskLogRetentionPending);
        Assert.Equal(originalRevision, await policy.RevisionAsync());
        Assert.Equal(7, (await PolicyAsync(fixture))["Days"].AsInt32);
        var ttl = Assert.Single(await IndexesAsync(fixture), value => value["name"] == "task_log_retention");
        Assert.Equal(7 * 86400, ttl["expireAfterSeconds"].ToInt64());
    }

    /// <summary>配置落盘后发布失败返回明确503，独立重建的服务可从文件恢复同一意图并清理。</summary>
    [MongoHttpFact]
    public async Task PublishFailurePreservesDurableIntentAndReplaysFromTheSettingsFile()
    {
        await using var fixture = await FixtureAsync();
        var settings = fixture.App.Services.GetRequiredService<ISettingsStore>();
        var context = new TaskLogRetentionFailureMongoContext(fixture.Context);
        using var failingPolicy = new TaskLogRetentionPolicy(context, settings);
        await failingPolicy.InitializeAsync();
        var originalRevision = await failingPolicy.RevisionAsync();
        var accesses = context.SuccessfulPolicyAccesses;
        var observedRevisionLookup = false;
        var failingSettings = new TaskLogRetentionFailureSettingsStore(settings, () =>
        {
            observedRevisionLookup = context.SuccessfulPolicyAccesses > accesses;
            context.FailPolicyAccess = true;
        });
        var service = new TaskLogRetentionSettingsService(failingSettings, failingPolicy);

        var error = await Assert.ThrowsAsync<InfrastructureException>(() => service.UpdateAsync(value =>
        {
            value.TaskLogRetentionDays = 0;
            return value;
        }));

        Assert.True(observedRevisionLookup);
        Assert.Equal(503, error.StatusCode);
        Assert.Equal("SETTINGS_TASK_LOG_RETENTION_PENDING", error.Code);
        Assert.Equal(1, failingSettings.Updates);
        Assert.True(context.FailedPolicyAccesses > 0);
        var durable = new StartupConfigurationStore(fixture.ConfigFile).ReadSettings();
        Assert.Equal(0, durable.TaskLogRetentionDays);
        Assert.NotNull(durable.TaskLogRetentionPending);
        var pending = durable.TaskLogRetentionPending;
        Assert.Equal(0, pending.Days);
        Assert.Equal(originalRevision, pending.ExpectedRevision);
        Assert.False(string.IsNullOrWhiteSpace(pending.ChangeId));
        var original = await PolicyAsync(fixture);
        Assert.Equal(7, original["Days"].AsInt32);
        Assert.Equal(originalRevision, original["Revision"].ToInt64());

        var reloadedSettings = new SettingsStore(new(fixture.ConfigFile), fixture.App.Services.GetRequiredService<IBootstrapStore>(),
            fixture.Context, fixture.App.Services.GetRequiredService<IAppPaths>());
        using var recoveredPolicy = new TaskLogRetentionPolicy(fixture.Context, reloadedSettings);
        var recovered = new TaskLogRetentionSettingsService(reloadedSettings, recoveredPolicy);
        await recovered.ReplayPendingAsync();

        var completed = new StartupConfigurationStore(fixture.ConfigFile).ReadSettings();
        Assert.Equal(0, completed.TaskLogRetentionDays);
        Assert.Null(completed.TaskLogRetentionPending);
        Assert.Null(await recoveredPolicy.CutoffAsync());
        var applied = await PolicyAsync(fixture);
        Assert.Equal(0, applied["Days"].AsInt32);
        Assert.Equal(pending.ChangeId, applied["ChangeId"].AsString);
        Assert.Equal(originalRevision + 1, applied["Revision"].ToInt64());
        Assert.Equal(applied["Revision"], applied["AppliedRevision"]);
        Assert.DoesNotContain(await IndexesAsync(fixture), value => value.GetValue("expireAfterSeconds", BsonNull.Value) != BsonNull.Value);
        await recovered.ReplayPendingAsync();
        Assert.Equal(applied["Revision"], (await PolicyAsync(fixture))["Revision"]);
    }

    /// <summary>策略生效后清理配置失败保留同一意图，恢复重放只清理标记而不重复增加版本。</summary>
    [MongoHttpFact]
    public async Task CleanupWriteFailureKeepsTheAppliedIntentForIdempotentReplay()
    {
        await using var fixture = await FixtureAsync();
        var settings = fixture.App.Services.GetRequiredService<ISettingsStore>();
        using var policy = new TaskLogRetentionPolicy(fixture.Context, settings);
        await policy.InitializeAsync();
        var originalRevision = await policy.RevisionAsync();
        var writes = 0;
        var backup = fixture.ConfigFile + ".saved";
        string? publishedChangeId = null;
        var failing = new TaskLogRetentionFailureSettingsStore(settings, () =>
        {
            if (++writes == 2)
            {
                publishedChangeId = new StartupConfigurationStore(fixture.ConfigFile).ReadSettings().TaskLogRetentionPending?.ChangeId;
                File.Move(fixture.ConfigFile, backup);
                Directory.CreateDirectory(fixture.ConfigFile);
            }
        });
        var service = new TaskLogRetentionSettingsService(failing, policy);
        try
        {
            var error = await Assert.ThrowsAsync<InfrastructureException>(() => service.UpdateAsync(value =>
            {
                value.TaskLogRetentionDays = 0;
                return value;
            }));
            Assert.Equal(503, error.StatusCode);
            Assert.Equal("SETTINGS_TASK_LOG_RETENTION_PENDING", error.Code);
        }
        finally
        {
            if (Directory.Exists(fixture.ConfigFile))
            {
                Directory.Delete(fixture.ConfigFile);
            }
            if (File.Exists(backup))
            {
                File.Move(backup, fixture.ConfigFile);
            }
        }

        Assert.Equal(2, failing.Updates);
        Assert.False(string.IsNullOrWhiteSpace(publishedChangeId));
        Assert.Empty(Directory.EnumerateFiles(fixture.DirectoryPath, "*.tmp"));
        var durable = new StartupConfigurationStore(fixture.ConfigFile).ReadSettings();
        Assert.Equal(0, durable.TaskLogRetentionDays);
        Assert.NotNull(durable.TaskLogRetentionPending);
        Assert.Equal(publishedChangeId, durable.TaskLogRetentionPending.ChangeId);
        Assert.Equal(originalRevision, durable.TaskLogRetentionPending.ExpectedRevision);
        var applied = await PolicyAsync(fixture);
        Assert.Equal(0, applied["Days"].AsInt32);
        Assert.Equal(publishedChangeId, applied["ChangeId"].AsString);
        Assert.Equal(originalRevision + 1, applied["Revision"].ToInt64());
        Assert.Equal(applied["Revision"], applied["AppliedRevision"]);
        Assert.Null(await policy.CutoffAsync());
        Assert.DoesNotContain(await IndexesAsync(fixture), value => value.GetValue("expireAfterSeconds", BsonNull.Value) != BsonNull.Value);

        var reloadedSettings = new SettingsStore(new(fixture.ConfigFile), fixture.App.Services.GetRequiredService<IBootstrapStore>(),
            fixture.Context, fixture.App.Services.GetRequiredService<IAppPaths>());
        using var recoveredPolicy = new TaskLogRetentionPolicy(fixture.Context, reloadedSettings);
        var recovered = new TaskLogRetentionSettingsService(reloadedSettings, recoveredPolicy);
        await recovered.ReplayPendingAsync();

        var completed = new StartupConfigurationStore(fixture.ConfigFile).ReadSettings();
        Assert.Equal(0, completed.TaskLogRetentionDays);
        Assert.Null(completed.TaskLogRetentionPending);
        var replayed = await PolicyAsync(fixture);
        Assert.Equal(applied["Revision"], replayed["Revision"]);
        Assert.Equal(applied["ChangeId"], replayed["ChangeId"]);
        await recovered.ReplayPendingAsync();
        Assert.Equal(applied["Revision"], (await PolicyAsync(fixture))["Revision"]);
    }

    private static Task<BsonDocument> PolicyAsync(HttpServerFixture fixture) =>
        fixture.Context.Collection<BsonDocument>("taskLogRetentionPolicies").Find(new BsonDocument("_id", "task-logs")).SingleAsync();

    private static async Task<List<BsonDocument>> IndexesAsync(HttpServerFixture fixture)
    {
        using var cursor = await fixture.Context.Collection<BsonDocument>("transcodeWorkerLogBatches").Indexes.ListAsync();
        return await cursor.ToListAsync();
    }

    private static async Task<HttpServerFixture> FixtureAsync()
    {
        var fixture = await HttpServerFixture.CreateAsync("Production", "--TranscodingControl:Enabled", "false");
        try
        {
            await fixture.InstallAsync();
            await fixture.Admin.LoginAsync(HttpServerFixture.ADMIN_EMAIL, HttpServerFixture.ADMIN_PASSWORD);
            foreach (var worker in fixture.App.Services.GetServices<IHostedService>().Where(worker => worker is MediaJobWorker or RecordingSupervisor or TaskLogRetentionWorker))
            {
                await worker.StopAsync(CancellationToken.None);
            }
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }
}
