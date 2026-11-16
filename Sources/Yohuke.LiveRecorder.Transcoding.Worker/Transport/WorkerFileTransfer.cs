using System.Net;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Transport;

internal sealed class WorkerFileTransfer : IDisposable
{
    private readonly WorkerTransferClient transfers;
    private readonly SemaphoreSlim[] inputGates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    internal WorkerFileTransfer(WorkerConnectionOptions options, HttpMessageHandler? handler = null, TimeSpan? networkTimeout = null)
    {
        transfers = new(options, "api/worker/transcoding/jobs", "X-Transcode-Attempt", "X-Transcode-Lease", handler, networkTimeout);
    }

    internal async Task DownloadAsync(TranscodeAssignment assignment, string path, long bytesPerSecond,
        Action<double> progress, CancellationToken cancellationToken,
        Action<WorkerTransferProgress>? transferProgress = null, Action<string>? log = null)
    {
        var gate = inputGates[(StringComparer.Ordinal.GetHashCode(path) & int.MaxValue) % inputGates.Length];
        var waiting = gate.WaitAsync(cancellationToken);
        if (!waiting.IsCompletedSuccessfully)
        {
            log?.Invoke("Waiting for another job to finish downloading the shared source.");
        }
        await waiting;
        try
        {
            log?.Invoke("Checking source length and version with the server.");
            using (var head = transfers.CreateRequest(HttpMethod.Head, Lease(assignment), "input"))
            using (var response = await transfers.SendHeadersAsync(head, cancellationToken))
            {
                await transfers.RequireSuccessAsync(head, response, cancellationToken);
                ValidateInput(response, assignment, false);
            }
            if (File.Exists(path) && new FileInfo(path).Length == assignment.SourceLength)
            {
                log?.Invoke("Reusing the verified cached source file.");
                progress(1);
                transferProgress?.Invoke(new(assignment.SourceLength, assignment.SourceLength, assignment.SourceFileName, 1, 1));
                return;
            }
            var partial = path + ".partial";
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await DownloadAttemptAsync(assignment, partial, bytesPerSecond, progress, cancellationToken, transferProgress, log);
                    File.Move(partial, path, overwrite: true);
                    return;
                }
                catch (Exception exception) when (attempt < 4 && !cancellationToken.IsCancellationRequested
                                                  && WorkerTransferClient.CanRetry(exception))
                {
                    log?.Invoke($"Download attempt {attempt + 1} failed. Retrying: {exception.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancellationToken);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DownloadAttemptAsync(TranscodeAssignment assignment, string partial, long bytesPerSecond,
        Action<double> progress, CancellationToken cancellationToken,
        Action<WorkerTransferProgress>? transferProgress, Action<string>? log)
    {
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > assignment.SourceLength)
        {
            File.Delete(partial);
            offset = 0;
        }
        if (offset == assignment.SourceLength)
        {
            progress(1);
            transferProgress?.Invoke(new(offset, assignment.SourceLength, assignment.SourceFileName, 1, 1));
            return;
        }
        if (offset > 0)
        {
            log?.Invoke(FormattableString.Invariant($"Resuming the source download at {offset} of {assignment.SourceLength} bytes."));
        }
        using var request = transfers.CreateRequest(HttpMethod.Get, Lease(assignment), "input");
        if (offset > 0)
        {
            request.Headers.Range = new(offset, null);
            if (!string.IsNullOrEmpty(assignment.SourceETag))
            {
                request.Headers.TryAddWithoutValidation("If-Range", assignment.SourceETag);
            }
        }
        using var response = await transfers.SendHeadersAsync(request, cancellationToken);
        await transfers.RequireSuccessAsync(request, response, cancellationToken);
        ValidateInput(response, assignment, true);
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (response.Content.Headers.ContentRange?.From != offset || response.Content.Headers.ContentRange?.Length != assignment.SourceLength)
            {
                throw new InvalidDataException("The server returned an unexpected download range.");
            }
        }
        else
        {
            if (offset > 0)
            {
                log?.Invoke("The server returned the complete source. Restarting the partial download.");
            }
            offset = 0;
        }
        transferProgress?.Invoke(new(offset, assignment.SourceLength, assignment.SourceFileName, 0, 1));
        await using var output = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Open,
            FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);
        output.Position = offset;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[65536];
        while (true)
        {
            var count = await transfers.ReadWithTimeoutAsync(input, buffer, cancellationToken);
            if (count == 0)
            {
                break;
            }
            if (offset + count > assignment.SourceLength)
            {
                throw new InvalidDataException("The server returned input exceeding the length declared by the job.");
            }
            await transfers.WaitForDownloadAsync(count, bytesPerSecond, cancellationToken);
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            offset += count;
            progress((double)offset / assignment.SourceLength);
            transferProgress?.Invoke(new(offset, assignment.SourceLength, assignment.SourceFileName,
                offset == assignment.SourceLength ? 1 : 0, 1));
        }
        await output.FlushAsync(cancellationToken);
        if (offset != assignment.SourceLength)
        {
            throw new EndOfStreamException("The source transfer ended early and will resume from the last completed offset.");
        }
    }

    internal async Task UploadAsync(TranscodeAssignment assignment, string directory, TranscodeOutputManifest manifest,
        long bytesPerSecond, Action<double> progress, CancellationToken cancellationToken,
        Action<WorkerTransferProgress>? transferProgress = null, Action<string>? log = null, Action? completing = null)
    {
        var total = manifest.Files.Sum(file => file.Length);
        var completed = 0L;
        var completedFiles = 0;
        var lease = Lease(assignment);
        log?.Invoke(FormattableString.Invariant($"Uploading {manifest.Files.Count} output files ({total} bytes)."));
        foreach (var artifact in manifest.Files)
        {
            await transfers.UploadFileAsync(lease, Path.Combine(directory, artifact.Name),
                "artifacts/" + Uri.EscapeDataString(artifact.Name), artifact.Length, bytesPerSecond, cancellationToken,
                sent =>
                {
                    progress((double)(completed + sent) / total);
                    transferProgress?.Invoke(new(completed + sent, total, artifact.Name, completedFiles, manifest.Files.Count));
                }, log);
            completed += artifact.Length;
            completedFiles++;
            progress((double)completed / total);
            transferProgress?.Invoke(new(completed, total, artifact.Name, completedFiles, manifest.Files.Count));
        }
        completing?.Invoke();
        await transfers.PostJsonAsync(lease, "complete", manifest, cancellationToken,
            TimeSpan.FromSeconds(Math.Max(60, assignment.SourceLength / (8d * 1024 * 1024))), 3, log);
    }

    internal Task ReportResultAsync(TranscodeAssignment assignment, WorkerJobResult result, CancellationToken cancellationToken) =>
        transfers.PostJsonAsync(Lease(assignment), "result", result, cancellationToken);

    private static WorkerTransferLease Lease(TranscodeAssignment assignment) => new()
    {
        JobId = assignment.JobId,
        Attempt = assignment.Attempt,
        LeaseToken = assignment.LeaseToken
    };

    private static void ValidateInput(HttpResponseMessage response, TranscodeAssignment assignment, bool ranged)
    {
        if (!string.IsNullOrEmpty(assignment.SourceETag) && response.Headers.ETag?.ToString() != assignment.SourceETag)
        {
            throw new WorkerLeaseLostException("The source version has changed.");
        }
        if ((!ranged || response.StatusCode != HttpStatusCode.PartialContent)
            && response.Content.Headers.ContentLength != assignment.SourceLength)
        {
            throw new WorkerLeaseLostException("The source length has changed.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        transfers.Dispose();
        foreach (var gate in inputGates)
        {
            gate.Dispose();
        }
    }
}
