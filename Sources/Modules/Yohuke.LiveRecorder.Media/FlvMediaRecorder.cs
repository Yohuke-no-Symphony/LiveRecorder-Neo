using System.Diagnostics;
using Microsoft.Extensions.Options;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.Media;

namespace Yohuke.LiveRecorder.Media;

/// <summary>Archives a live HTTP FLV connection through the upstream repair pipeline.</summary>
public sealed class FlvMediaRecorder : IMediaRecorder
{
    private readonly HttpClient client;
    private readonly MediaToolOptions options;
    private readonly ISettingsStore settings;
    private readonly TimeProvider timeProvider;

    /// <summary>Creates the recorder using the configured HTTP client and stream deadline.</summary>
    public FlvMediaRecorder(HttpClient client, IOptions<MediaToolOptions> options, ISettingsStore settings, TimeProvider? timeProvider = null)
    {
        this.client = client;
        this.options = options.Value;
        this.settings = settings;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task RecordAsync(RecordingRequest request, IProgress<MediaRecordingUpdate> progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(request.OutputDirectory);
        var capacityClock = Stopwatch.StartNew();
        var checkedCapacity = false;
        async Task CheckCapacityAsync(CancellationToken token)
        {
            if (checkedCapacity && capacityClock.Elapsed < TimeSpan.FromSeconds(3))
            {
                return;
            }
            var configuration = await settings.GetAsync(token);
            var available = new DriveInfo(Path.GetFullPath(request.OutputDirectory)).AvailableFreeSpace;
            if (available < configuration.MinimumFreeBytes)
            {
                throw new InsufficientMediaSpaceException(available, configuration.MinimumFreeBytes);
            }
            checkedCapacity = true;
            capacityClock.Restart();
        }
        await CheckCapacityAsync(cancellationToken);
        using var message = new HttpRequestMessage(HttpMethod.Get, request.StreamUrl);
        foreach (var header in request.Headers)
        {
            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        using var connectionDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectionDeadline.CancelAfter(options.StreamConnectTimeout);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, connectionDeadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The live stream connection did not return headers before its deadline.");
        }
        connectionDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
        using var responseLifetime = response;
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var deadline = new ReadTimeoutStream(source, options.StreamReadTimeout);
        await FlvArchiveEngine.RunAsync(deadline, request, progress, cancellationToken, CheckCapacityAsync, collectOutputs: false, timeProvider);
    }
}
