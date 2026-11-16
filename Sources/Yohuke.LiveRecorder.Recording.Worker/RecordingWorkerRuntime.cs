using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Yohuke.LiveRecorder.Application.Media;
using Yohuke.LiveRecorder.Media;
using Yohuke.LiveRecorder.Recording.Contracts;
using Yohuke.LiveRecorder.Recording.Worker.Capture;
using Yohuke.LiveRecorder.Recording.Worker.Storage;
using Yohuke.LiveRecorder.Recording.Worker.Transport;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Execution;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;

namespace Yohuke.LiveRecorder.Recording.Worker;

internal sealed class RecordingWorkerRuntime
{
    private readonly WorkerConnectionOptions options;
    private readonly WorkerSettingsStore settings;
    private readonly Action<string> log;
    private readonly Func<IRecordingCaptureSource> captureFactory;
    private readonly Func<WorkerConfiguration, IMediaRecorder>? recorderFactory;
    private readonly TimeProvider time;
    private readonly HttpMessageHandler? transferHandler;
    private readonly ConcurrentDictionary<string, RecordingRoomRunner> rooms = new(StringComparer.Ordinal);
    private readonly WorkerLogBuffer logs = new();
    private WorkerConfiguration configuration;
    private int appliedRevision;
    private string? configurationError;

    internal RecordingWorkerRuntime(WorkerConnectionOptions options, WorkerSettingsStore settings, Action<string>? log = null,
        Func<IRecordingCaptureSource>? captureFactory = null, Func<WorkerConfiguration, IMediaRecorder>? recorderFactory = null,
        TimeProvider? timeProvider = null, HttpMessageHandler? transferHandler = null)
    {
        this.options = options;
        this.settings = settings;
        this.log = log ?? Console.WriteLine;
        this.captureFactory = captureFactory ?? (() => new BilibiliRecordingCaptureSource());
        this.recorderFactory = recorderFactory;
        time = timeProvider ?? TimeProvider.System;
        this.transferHandler = transferHandler;
        configuration = settings.Current.Configuration with { };
        appliedRevision = settings.Current.ConfigurationRevision;
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        options.Validate();
        WorkerConfigurationValidation.Validate(configuration, "Recording");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var media = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var spool = new RecordingSpool(settings.Path, options.WorkerId, configuration.TempDirectory, WriteLog);
        using var transfers = new RecordingFileTransfer(options, transferHandler);
        await spool.RecoverAsync(lifetime.Token);
        IMediaRecorder Recorder(WorkerConfiguration selected) => recorderFactory?.Invoke(selected)
            ?? new FlvMediaRecorder(media, Options.Create(new MediaToolOptions()), new RecordingMediaSettingsStore(selected));
        RefreshRooms(spool, Recorder, lifetime.Token);
        var supervisor = SuperviseRoomsAsync(spool, Recorder, lifetime);
        var uploader = new RecordingSpoolUploader(spool, transfers, () => Volatile.Read(ref configuration),
            id => !rooms.TryGetValue(id, out var room) || room.IsStopped, WriteLog).RunAsync(lifetime.Token);
        try
        {
            var delay = 1;
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    await using var control = await WorkerControlClient.ConnectAsync(options, lifetime.Token);
                    var welcome = await control.ExchangeAsync<WorkerHello, WorkerWelcome>("Hello", "Welcome", new()
                    {
                        Kind = "Recording", WorkerId = options.WorkerId, Secret = options.Secret, Capabilities = Capabilities()
                    }, lifetime.Token);
                    await ApplyConfigurationAsync(welcome.Configuration, welcome.ConfigurationRevision, spool, lifetime.Token);
                    delay = 1;
                    WriteLog("Connected to the recording coordinator. Durable room ownership remains independent of the connection.");
                    while (!lifetime.IsCancellationRequested)
                    {
                        var progress = rooms.Values.Select(room => room.Snapshot()).Take(64).ToList();
                        var state = await control.ExchangeAsync<WorkerHeartbeat, WorkerState>("Heartbeat", "State", new()
                        {
                            AppliedConfigurationRevision = appliedRevision, ConfigurationError = configurationError,
                            Capabilities = Capabilities(), Recordings = progress,
                            Telemetry = new()
                            {
                                ActiveRecordings = progress.Count(room => room.State == "Recording"),
                                SpoolBytes = spool.Bytes(), PendingUploadBytes = spool.PendingBytes(),
                                FreeBytes = spool.FreeBytes(configuration.TempDirectory)
                            },
                            LogBatch = welcome.LogProtocolVersion >= 1 ? logs.GetBatch() : null
                        }, lifetime.Token);
                        logs.Acknowledge(state.LogAcknowledgement);
                        await ApplyConfigurationAsync(state.Configuration, state.ConfigurationRevision, spool, lifetime.Token);
                        var known = spool.Assignments().ToDictionary(value => value.Assignment.AssignmentId, StringComparer.Ordinal);
                        foreach (var lease in state.RecordingAssignments)
                        {
                            if (!known.TryGetValue(lease.AssignmentId, out var existing)
                                || existing.Assignment.Generation != lease.Generation
                                || existing.Assignment.ConfigurationRevision != lease.ConfigurationRevision
                                || existing.Assignment.Enabled != lease.Enabled || existing.Assignment.Token != lease.Token)
                            {
                                var assignment = await transfers.AssignmentAsync(lease, lifetime.Token);
                                spool.SaveAssignment(assignment, configuration.TempDirectory);
                            }
                        }
                        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(welcome.HeartbeatSeconds, 1, 10)), time, lifetime.Token);
                    }
                }
                catch (WorkerAuthenticationException)
                {
                    WriteLog("Worker credentials were revoked. Capture is stopping; all unacknowledged spool data is preserved.");
                    throw;
                }
                catch (Exception error) when (!lifetime.IsCancellationRequested && error is IOException or SocketException
                    or AuthenticationException or OperationCanceledException or InvalidOperationException or HttpRequestException or JsonException)
                {
                    WriteLog("The coordinator is unavailable. Local recording and durable queueing continue (" + error.GetType().Name + ").");
                    await Task.Delay(TimeSpan.FromSeconds(delay), time, lifetime.Token);
                    delay = Math.Min(30, delay * 2);
                }
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            try
            {
                await Task.WhenAll(rooms.Values.Select(room => room.Completion).Append(supervisor).Append(uploader));
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                foreach (var room in rooms.Values)
                {
                    room.Dispose();
                }
                rooms.Clear();
            }
        }
    }

    private async Task SuperviseRoomsAsync(RecordingSpool spool, Func<WorkerConfiguration, IMediaRecorder> recorders,
        CancellationTokenSource lifetime)
    {
        var cancellationToken = lifetime.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), time, cancellationToken);
                RefreshRooms(spool, recorders, cancellationToken);
            }
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            WriteLog("Local recording supervision failed; capture is stopping and the spool is preserved (" + error.GetType().Name + ").");
            await lifetime.CancelAsync();
            throw;
        }
    }

    private void RefreshRooms(RecordingSpool spool, Func<WorkerConfiguration, IMediaRecorder> recorders,
        CancellationToken cancellationToken)
    {
        foreach (var saved in spool.Assignments())
        {
            var assignment = saved.Assignment;
            if (rooms.TryGetValue(assignment.AssignmentId, out var running))
            {
                running.Update(assignment);
                if (saved.Released && running.IsStopped && rooms.TryRemove(assignment.AssignmentId, out var removed))
                {
                    removed.Dispose();
                }
                if (!running.IsStopped || saved.Released || !assignment.Enabled)
                {
                    continue;
                }
                _ = running.Completion.Exception;
                if (rooms.TryRemove(assignment.AssignmentId, out var stopped))
                {
                    stopped.Dispose();
                    WriteLog("A recording room task ended. Its durable session will resume in a new capture attempt.");
                }
            }
            if (saved.Released || !assignment.Enabled && spool.Sessions(assignment.AssignmentId).Count == 0
                || assignment.Enabled && rooms.Values.Count(room => !room.IsStopped) >= configuration.MaxConcurrentEncodes)
            {
                continue;
            }
            var created = new RecordingRoomRunner(assignment, spool, captureFactory(), recorders,
                () => Volatile.Read(ref configuration), time, WriteLog, cancellationToken);
            if (rooms.TryAdd(assignment.AssignmentId, created))
            {
                created.Start();
            }
            else
            {
                created.Dispose();
            }
        }
    }

    private async Task ApplyConfigurationAsync(WorkerConfiguration value, int revision, RecordingSpool spool, CancellationToken cancellationToken)
    {
        try
        {
            WorkerConfigurationValidation.Validate(value, "Recording");
            spool.EnsureRoot(value.TempDirectory);
            await settings.SaveRemoteConfigurationAsync(value, revision, cancellationToken);
            Volatile.Write(ref configuration, value with { });
            appliedRevision = revision;
            configurationError = null;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            configurationError = "Recording configuration could not be applied (" + error.GetType().Name + ").";
            WriteLog(configurationError);
        }
    }

    private static WorkerCapabilities Capabilities() => new()
    {
        RecordingVersion = 1, OperatingSystem = RuntimeInformation.OSDescription,
        Architecture = RuntimeInformation.ProcessArchitecture.ToString()
    };

    private void WriteLog(string message)
    {
        logs.Append(new() { Message = message });
        log("[" + time.GetUtcNow().ToString("O") + "] " + message);
    }
}
