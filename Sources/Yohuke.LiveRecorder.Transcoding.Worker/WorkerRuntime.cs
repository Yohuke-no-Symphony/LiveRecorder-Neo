using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Authentication;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Encoding;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;
using Yohuke.LiveRecorder.Transcoding.Worker.Resources;
using Yohuke.LiveRecorder.Transcoding.Worker.Scheduling;
using Yohuke.LiveRecorder.Transcoding.Worker.Storage;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;

namespace Yohuke.LiveRecorder.Transcoding.Worker;

internal sealed class WorkerRuntime
{
    private readonly WorkerConnectionOptions options;
    private readonly WorkerSettingsStore settings;
    private readonly WorkerProcessRunner processes = new();
    private readonly CpuUsageMonitor cpu = new();
    private readonly EncodingAdmission admission = new();
    private readonly ConcurrentDictionary<string, WorkerRunningJob> jobs = new(StringComparer.Ordinal);
    private readonly WorkerConfigurationManager configurations;
    private readonly WorkerWorkspace workspace;
    private readonly WorkerFileTransfer transfers;
    private readonly MediaInspector inspector;

    internal WorkerRuntime(WorkerConnectionOptions options, WorkerSettingsStore settings)
    {
        this.options = options;
        this.settings = settings;
        workspace = new(options.WorkerId);
        transfers = new(options);
        inspector = new(processes);
        configurations = new(processes, configuration => admission.TryAcquireProbe(configuration));
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sampling = cpu.RunAsync(lifetime.Token);
        var monitoring = MonitorLeasesAsync(lifetime.Token);
        try
        {
            var reconnectDelay = 1;
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    await using var control = await WorkerControlClient.ConnectAsync(options, lifetime.Token);
                    var welcome = await control.ExchangeAsync<WorkerHello, WorkerWelcome>("Hello", "Welcome", new()
                    {
                        WorkerId = options.WorkerId,
                        Secret = options.Secret,
                        Capabilities = configurations.Current.Capabilities
                    }, lifetime.Token);
                    await settings.SaveRemoteConfigurationAsync(welcome.Configuration, welcome.ConfigurationRevision, lifetime.Token);
                    configurations.Request(welcome.Configuration, welcome.ConfigurationRevision, lifetime.Token);
                    reconnectDelay = 1;
                    Log("已连接主站，配置版本 " + welcome.ConfigurationRevision);
                    while (!lifetime.IsCancellationRequested)
                    {
                        configurations.RefreshCapabilitiesWhenDue(lifetime.Token);
                        var current = configurations.Current;
                        var state = await control.ExchangeAsync<WorkerHeartbeat, WorkerState>("Heartbeat", "State", new()
                        {
                            AppliedConfigurationRevision = current.Revision,
                            ConfigurationError = configurations.Error,
                            Capabilities = current.Capabilities,
                            Telemetry = Telemetry(current.Configuration),
                            Jobs = jobs.Values.Select(job => job.Snapshot()).ToList()
                        }, lifetime.Token);
                        await settings.SaveRemoteConfigurationAsync(state.Configuration, state.ConfigurationRevision, lifetime.Token);
                        configurations.Request(state.Configuration, state.ConfigurationRevision, lifetime.Token);
                        foreach (var lease in state.Leases)
                        {
                            if (jobs.TryGetValue(lease.JobId, out var job) && job.Assignment.Attempt == lease.Attempt)
                            {
                                if (lease.Accepted)
                                {
                                    job.Renew(lease.LeaseSeconds);
                                }
                                else
                                {
                                    job.Cancel();
                                }
                            }
                        }
                        foreach (var assignment in state.Assignments)
                        {
                            await StartAsync(assignment, lifetime.Token);
                        }
                        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(welcome.HeartbeatSeconds, 1, 10)), lifetime.Token);
                    }
                }
                catch (WorkerAuthenticationException)
                {
                    lifetime.Cancel();
                    throw;
                }
                catch (Exception exception) when (!lifetime.IsCancellationRequested && exception is IOException or SocketException
                    or AuthenticationException or InvalidOperationException or OperationCanceledException)
                {
                    Log("控制连接中断，等待重连：" + exception.Message);
                    await Task.Delay(TimeSpan.FromSeconds(reconnectDelay), lifetime.Token);
                    reconnectDelay = Math.Min(30, reconnectDelay * 2);
                }
            }
        }
        finally
        {
            lifetime.Cancel();
            foreach (var job in jobs.Values)
            {
                job.Cancel();
            }
            await Task.WhenAll(jobs.Values.Select(job => job.Task));
            await configurations.StopAsync();
            try
            {
                await Task.WhenAll(sampling, monitoring);
            }
            catch (OperationCanceledException)
            {
            }
            transfers.Dispose();
        }
    }

    private async Task StartAsync(TranscodeAssignment assignment, CancellationToken cancellationToken)
    {
        if (jobs.TryGetValue(assignment.JobId, out var existing))
        {
            if (existing.Assignment.Attempt == assignment.Attempt && existing.Assignment.LeaseToken == assignment.LeaseToken)
            {
                return;
            }
            existing.Cancel();
            await existing.Task;
        }
        var job = new WorkerRunningJob(assignment, cancellationToken);
        if (!jobs.TryAdd(assignment.JobId, job))
        {
            job.Dispose();
            return;
        }
        job.Task = RunJobAsync(job);
    }

    private async Task RunJobAsync(WorkerRunningJob job)
    {
        await Task.Yield();
        var token = job.Cancellation.Token;
        try
        {
            var initial = configurations.Current;
            using var files = workspace.Acquire(initial.Configuration, job.Assignment);
            Log("准备任务 " + job.Assignment.JobId + "，档位 " + job.Assignment.Profile.Name);
            await transfers.DownloadAsync(job.Assignment, files.SourcePath, initial.Configuration.DownloadBytesPerSecond,
                value => job.Update("Downloading", value * 10), token);
            var source = await inspector.InspectInputAsync(initial.Configuration.FfprobePath, files.SourcePath, token);
            _ = TranscodePlanner.Create(files.SourcePath, files.OutputDirectory, source, job.Assignment.Profile,
                new() { Encoder = "libx264", Available = true }, 1);
            var waiting = Stopwatch.StartNew();
            job.Update("WaitingForGpu", 15);
            EncodingReservation? reservation = null;
            WorkerRuntimeConfiguration selected;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                selected = configurations.Current;
                if (configurations.ConfigurationRejected)
                {
                    await transfers.ReportResultAsync(job.Assignment, new()
                    {
                        State = "Queued", ErrorCode = "WorkerCapacityUnavailable", Error = configurations.Error
                    }, token);
                    return;
                }
                if (configurations.Error is null)
                {
                    var resources = cpu.Snapshot(selected.Configuration.CpuSampleWindowSeconds);
                    if (!EncodingAdmission.HasPossibleCapacity(selected.Configuration, selected.Capabilities, resources)
                        && (selected.Revision != initial.Revision || waiting.Elapsed > TimeSpan.FromSeconds(
                            selected.Configuration.GpuWaitSeconds + selected.Configuration.CpuSampleWindowSeconds)))
                    {
                        await transfers.ReportResultAsync(job.Assignment, new()
                        {
                            State = "Queued", ErrorCode = "WorkerCapacityUnavailable", Error = "节点配置或设备能力已变化，释放任务等待可用节点"
                        }, token);
                        Log("任务退队等待可用资源 " + job.Assignment.JobId);
                        return;
                    }
                    reservation = admission.TryAcquire(selected.Configuration, selected.Capabilities,
                        resources, waiting.Elapsed);
                }
                if (reservation is not null)
                {
                    break;
                }
                await Task.Delay(250, token);
            }
            TranscodeOutputManifest manifest;
            using (reservation)
            {
                var plan = TranscodePlanner.Create(files.SourcePath, files.OutputDirectory, source,
                    job.Assignment.Profile, reservation.Encoder, reservation.Threads);
                job.Update("Encoding", 15, reservation.Encoder.Encoder);
                Log("开始编码 " + job.Assignment.JobId + "：" + reservation.Encoder.Encoder);
                var result = await processes.RunAsync(selected.Configuration.FfmpegPath, plan.Arguments, token,
                    value => job.Update("Encoding", 15 + value * 65), source.DurationMs / 1000);
                if (result.ExitCode != 0)
                {
                    configurations.InvalidateEncoder(reservation.Encoder);
                    throw new InvalidDataException("FFmpeg 编码失败：" + result.Diagnostic);
                }
                job.Update("Validating", 80);
                manifest = await new TranscodeOutputValidator(inspector).ValidateAsync(selected.Configuration.FfprobePath,
                    files.OutputDirectory, source, plan.ExpectedMedia, reservation.Encoder.Encoder, token);
            }
            job.Update("Uploading", 85);
            await transfers.UploadAsync(job.Assignment, files.OutputDirectory, manifest, selected.Configuration.UploadBytesPerSecond,
                value => job.Update("Uploading", 85 + value * 14), token);
            Log("已完成任务 " + job.Assignment.JobId);
        }
        catch (OperationCanceledException)
        {
            Log("已停止任务 " + job.Assignment.JobId);
        }
        catch (Exception exception)
        {
            Log("任务未完成 " + job.Assignment.JobId + "：" + exception.Message);
            try
            {
                await transfers.ReportResultAsync(job.Assignment, new()
                {
                    State = exception is NotSupportedException ? "Skipped" : "Failed",
                    ErrorCode = exception is NotSupportedException ? exception.Message : "WorkerExecutionFailed",
                    Error = exception.Message.Length > 4000 ? exception.Message[..4000] : exception.Message
                }, token);
            }
            catch (Exception reportError) when (reportError is HttpRequestException or IOException or OperationCanceledException)
            {
                Log("无法提交任务终态，等待主站回收租约：" + reportError.Message);
            }
        }
        finally
        {
            jobs.TryRemove(new KeyValuePair<string, WorkerRunningJob>(job.Assignment.JobId, job));
            job.Dispose();
        }
    }

    private WorkerTelemetry Telemetry(WorkerConfiguration configuration)
    {
        var snapshot = cpu.Snapshot(configuration.CpuSampleWindowSeconds);
        var running = admission.Running;
        var free = 0L;
        try
        {
            if (!string.IsNullOrWhiteSpace(configuration.TempDirectory))
            {
                free = WorkerWorkspace.FreeBytes(configuration.TempDirectory);
            }
        }
        catch (IOException)
        {
        }
        return new()
        {
            CpuPercent = snapshot.AveragePercent,
            CpuThreadBudget = EncodingAdmission.ThreadBudget(configuration, snapshot),
            CpuUnavailableReason = snapshot.UnavailableReason,
            RunningCpu = running.Cpu,
            RunningGpu = running.Gpu,
            FreeBytes = free
        };
    }

    private async Task MonitorLeasesAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            foreach (var job in jobs.Values)
            {
                job.CheckLease();
            }
        }
    }

    private static void Log(string message) => Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {message}");
}
