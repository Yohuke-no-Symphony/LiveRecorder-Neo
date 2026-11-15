using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Transport;

internal sealed class WorkerFileTransfer : IDisposable
{
    private const int UPLOAD_CHUNK_BYTES = 8 * 1024 * 1024;
    private readonly WorkerConnectionOptions options;
    private readonly HttpClient client;
    private readonly TimeSpan networkTimeout;
    private readonly TransferThrottle downloadThrottle = new();
    private readonly TransferThrottle uploadThrottle = new();
    private readonly SemaphoreSlim[] inputGates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    internal WorkerFileTransfer(WorkerConnectionOptions options, HttpMessageHandler? handler = null, TimeSpan? networkTimeout = null)
    {
        this.options = options;
        this.networkTimeout = networkTimeout ?? TimeSpan.FromSeconds(30);
        handler ??= new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            SslOptions = new()
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) => WorkerCertificatePolicy.Validate(options, certificate, errors, true)
            }
        };
        client = new(handler) { BaseAddress = new(options.ApiBaseUrl.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    internal async Task DownloadAsync(TranscodeAssignment assignment, string path, long bytesPerSecond,
        Action<double> progress, CancellationToken cancellationToken)
    {
        var gate = inputGates[(StringComparer.Ordinal.GetHashCode(path) & int.MaxValue) % inputGates.Length];
        await gate.WaitAsync(cancellationToken);
        try
        {
            using (var head = Request(HttpMethod.Head, assignment, "input"))
            using (var response = await SendHeadersAsync(head, cancellationToken))
            {
                RequireSuccess(response);
                ValidateInput(response, assignment, false);
            }
            if (File.Exists(path) && new FileInfo(path).Length == assignment.SourceLength)
            {
                progress(1);
                return;
            }
            var partial = path + ".partial";
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await DownloadAttemptAsync(assignment, partial, bytesPerSecond, progress, cancellationToken);
                    File.Move(partial, path, overwrite: true);
                    return;
                }
                catch (Exception exception) when (attempt < 4 && !cancellationToken.IsCancellationRequested
                                                  && exception is HttpRequestException or IOException)
                {
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
        Action<double> progress, CancellationToken cancellationToken)
    {
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > assignment.SourceLength)
        {
            File.Delete(partial);
            offset = 0;
        }
        if (offset == assignment.SourceLength)
        {
            return;
        }
        using var request = Request(HttpMethod.Get, assignment, "input");
        if (offset > 0)
        {
            request.Headers.Range = new(offset, null);
            if (!string.IsNullOrEmpty(assignment.SourceETag))
            {
                request.Headers.TryAddWithoutValidation("If-Range", assignment.SourceETag);
            }
        }
        using var response = await SendHeadersAsync(request, cancellationToken);
        RequireSuccess(response);
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
            offset = 0;
        }
        await using var output = new FileStream(partial, offset == 0 ? FileMode.Create : FileMode.Open,
            FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);
        output.Position = offset;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[65536];
        while (true)
        {
            var count = await ReadWithTimeoutAsync(input, buffer, cancellationToken);
            if (count == 0)
            {
                break;
            }
            if (offset + count > assignment.SourceLength)
            {
                throw new InvalidDataException("The server returned input exceeding the length declared by the job.");
            }
            await downloadThrottle.WaitAsync(count, bytesPerSecond, cancellationToken);
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            offset += count;
            progress((double)offset / assignment.SourceLength);
        }
        await output.FlushAsync(cancellationToken);
        if (offset != assignment.SourceLength)
        {
            throw new EndOfStreamException("The source transfer ended early and will resume from the last completed offset.");
        }
    }

    internal async Task UploadAsync(TranscodeAssignment assignment, string directory, TranscodeOutputManifest manifest,
        long bytesPerSecond, Action<double> progress, CancellationToken cancellationToken)
    {
        var total = manifest.Files.Sum(file => file.Length);
        var completed = 0L;
        foreach (var artifact in manifest.Files)
        {
            var endpoint = "artifacts/" + Uri.EscapeDataString(artifact.Name);
            var offset = await UploadOffsetAsync(assignment, endpoint, artifact.Length, cancellationToken);
            var retries = 0;
            await using var source = new FileStream(Path.Combine(directory, artifact.Name), FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            while (offset < artifact.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    source.Position = offset;
                    var count = Math.Min(UPLOAD_CHUNK_BYTES, artifact.Length - offset);
                    using var request = Request(HttpMethod.Put, assignment, endpoint);
                    using var timeout = new TransferProgressTimeout(networkTimeout, cancellationToken);
                    request.Headers.TryAddWithoutValidation("Upload-Length", artifact.Length.ToString(CultureInfo.InvariantCulture));
                    request.Headers.TryAddWithoutValidation("Upload-Offset", offset.ToString(CultureInfo.InvariantCulture));
                    request.Content = new UploadChunkContent(source, count, uploadThrottle, bytesPerSecond, timeout);
                    request.Content.Headers.ContentType = new("application/octet-stream");
                    using var response = await SendUploadAsync(request, timeout.Token, cancellationToken);
                    if (response.StatusCode == HttpStatusCode.Conflict)
                    {
                        if (++retries > 4)
                        {
                            throw new IOException("The upload offset repeatedly conflicted with the server offset.");
                        }
                        offset = await UploadOffsetAsync(assignment, endpoint, artifact.Length, cancellationToken);
                        continue;
                    }
                    RequireSuccess(response);
                    var next = Offset(response, artifact.Length);
                    if (next != offset + count)
                    {
                        throw new InvalidDataException("The server acknowledged an unexpected upload offset.");
                    }
                    offset = next;
                    retries = 0;
                    progress((double)(completed + offset) / total);
                }
                catch (Exception exception) when (++retries <= 4 && !cancellationToken.IsCancellationRequested
                                                  && exception is HttpRequestException or IOException)
                {
                    await Task.Delay(TimeSpan.FromSeconds(retries), cancellationToken);
                    offset = await UploadOffsetAsync(assignment, endpoint, artifact.Length, cancellationToken);
                }
            }
            completed += artifact.Length;
            progress((double)completed / total);
        }
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var complete = Request(HttpMethod.Post, assignment, "complete");
                complete.Content = JsonContent.Create(manifest, options: WorkerProtocol.JsonOptions);
                using var completion = await SendHeadersAsync(complete, cancellationToken,
                    TimeSpan.FromSeconds(Math.Max(60, assignment.SourceLength / (8d * 1024 * 1024))));
                RequireSuccess(completion);
                break;
            }
            catch (Exception exception) when (attempt < 3 && !cancellationToken.IsCancellationRequested
                                              && exception is HttpRequestException or IOException)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancellationToken);
            }
        }
    }

    internal async Task ReportResultAsync(TranscodeAssignment assignment, WorkerJobResult result, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, assignment, "result");
        request.Content = JsonContent.Create(result, options: WorkerProtocol.JsonOptions);
        using var response = await SendHeadersAsync(request, cancellationToken);
        RequireSuccess(response);
    }

    private async Task<long> UploadOffsetAsync(TranscodeAssignment assignment, string endpoint, long length, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Head, assignment, endpoint);
        request.Headers.TryAddWithoutValidation("Upload-Length", length.ToString(CultureInfo.InvariantCulture));
        using var response = await SendHeadersAsync(request, cancellationToken);
        RequireSuccess(response);
        return Offset(response, length);
    }

    private HttpRequestMessage Request(HttpMethod method, TranscodeAssignment assignment, string endpoint)
    {
        var request = new HttpRequestMessage(method, "api/worker/transcoding/jobs/" + Uri.EscapeDataString(assignment.JobId) + "/" + endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Secret);
        request.Headers.Add("X-Worker-Id", options.WorkerId);
        request.Headers.Add("X-Transcode-Attempt", assignment.Attempt.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-Transcode-Lease", assignment.LeaseToken);
        return request;
    }

    private async Task<HttpResponseMessage> SendHeadersAsync(HttpRequestMessage request, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? networkTimeout);
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Timed out waiting for the server response.");
        }
    }

    private async Task<HttpResponseMessage> SendUploadAsync(HttpRequestMessage request, CancellationToken transferToken, CancellationToken jobToken)
    {
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, transferToken);
        }
        catch (OperationCanceledException) when (!jobToken.IsCancellationRequested)
        {
            throw new IOException("The upload timed out without network progress.");
        }
    }

    private async Task<int> ReadWithTimeoutAsync(Stream input, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(networkTimeout);
        try
        {
            return await input.ReadAsync(buffer, limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The download timed out without network progress.");
        }
    }

    private static long Offset(HttpResponseMessage response, long total)
    {
        if (!response.Headers.TryGetValues("Upload-Offset", out var values)
            || !long.TryParse(values.SingleOrDefault(), CultureInfo.InvariantCulture, out var offset) || offset < 0 || offset > total)
        {
            throw new InvalidDataException("The server returned an invalid upload resume offset.");
        }
        return offset;
    }

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

    private static void RequireSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
            or HttpStatusCode.Gone or HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            throw new WorkerLeaseLostException("The server rejected the current job lease or worker credentials (status " + (int)response.StatusCode + ").");
        }
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        client.Dispose();
        foreach (var gate in inputGates)
        {
            gate.Dispose();
        }
    }
}
