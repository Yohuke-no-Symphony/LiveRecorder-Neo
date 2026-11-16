using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Transport;

internal sealed class WorkerTransferClient : IDisposable
{
    private const int UPLOAD_CHUNK_BYTES = 8 * 1024 * 1024;
    private const int MAX_ERROR_RESPONSE_BYTES = 16 * 1024;
    private readonly WorkerConnectionOptions options;
    private readonly string basePath;
    private readonly string attemptHeader;
    private readonly string leaseHeader;
    private readonly HttpClient client;
    private readonly TimeSpan networkTimeout;
    private readonly TransferThrottle downloadThrottle = new();
    private readonly TransferThrottle uploadThrottle = new();

    internal WorkerTransferClient(WorkerConnectionOptions options, string basePath, string attemptHeader, string leaseHeader,
        HttpMessageHandler? handler = null, TimeSpan? networkTimeout = null)
    {
        this.options = options;
        this.basePath = basePath.Trim('/') + "/";
        this.attemptHeader = attemptHeader;
        this.leaseHeader = leaseHeader;
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

    internal HttpRequestMessage CreateRequest(HttpMethod method, WorkerTransferLease lease, string endpoint)
    {
        var request = new HttpRequestMessage(method, basePath + Uri.EscapeDataString(lease.JobId) + "/" + endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Secret);
        request.Headers.Add("X-Worker-Id", options.WorkerId);
        request.Headers.Add(attemptHeader, lease.Attempt.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(leaseHeader, lease.LeaseToken);
        return request;
    }

    internal async Task<HttpResponseMessage> SendHeadersAsync(HttpRequestMessage request, CancellationToken cancellationToken,
        TimeSpan? timeout = null)
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

    internal async Task<int> ReadWithTimeoutAsync(Stream input, Memory<byte> buffer, CancellationToken cancellationToken)
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

    internal Task WaitForDownloadAsync(int bytes, long bytesPerSecond, CancellationToken cancellationToken) =>
        downloadThrottle.WaitAsync(bytes, bytesPerSecond, cancellationToken);

    internal Task UploadFileAsync(WorkerTransferLease lease, string path, string endpoint, long length,
        long bytesPerSecond, CancellationToken cancellationToken, Action<long>? progress = null, Action<string>? log = null) =>
        UploadFileCoreAsync(lease, path, endpoint, length, bytesPerSecond, cancellationToken, true, progress, log);

    internal Task AppendFileAsync(WorkerTransferLease lease, string path, string endpoint, long committedLength,
        long bytesPerSecond, CancellationToken cancellationToken, Action<long>? progress = null, Action<string>? log = null) =>
        UploadFileCoreAsync(lease, path, endpoint, committedLength, bytesPerSecond, cancellationToken, false, progress, log);

    private async Task UploadFileCoreAsync(WorkerTransferLease lease, string path, string endpoint, long length,
        long bytesPerSecond, CancellationToken cancellationToken, bool sealedFile,
        Action<long>? progress, Action<string>? log)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        var offset = await UploadOffsetAsync(lease, endpoint, length, cancellationToken);
        progress?.Invoke(offset);
        if (offset > 0 && offset < length)
        {
            log?.Invoke(FormattableString.Invariant($"Resuming upload of {Path.GetFileName(path)} at {offset} of {length} bytes."));
        }
        var retries = 0;
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read,
            sealedFile ? FileShare.Read : FileShare.ReadWrite | FileShare.Delete,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (sealedFile ? source.Length != length : source.Length < length)
        {
            throw new InvalidDataException("The output artifact length changed before upload.");
        }
        while (offset < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                source.Position = offset;
                var count = Math.Min(UPLOAD_CHUNK_BYTES, length - offset);
                using var request = CreateRequest(HttpMethod.Put, lease, endpoint);
                using var timeout = new TransferProgressTimeout(networkTimeout, cancellationToken);
                request.Headers.TryAddWithoutValidation("Upload-Length", length.ToString(CultureInfo.InvariantCulture));
                request.Headers.TryAddWithoutValidation("Upload-Offset", offset.ToString(CultureInfo.InvariantCulture));
                var chunkOffset = offset;
                request.Content = new UploadChunkContent(source, count, uploadThrottle, bytesPerSecond, timeout,
                    sent => progress?.Invoke(chunkOffset + sent));
                request.Content.Headers.ContentType = new("application/octet-stream");
                using var response = await SendUploadAsync(request, timeout.Token, cancellationToken);
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    log?.Invoke("Upload offset changed for " + Path.GetFileName(path) + ". Synchronizing the resume offset.");
                    if (++retries > 4)
                    {
                        throw new IOException("The upload offset repeatedly conflicted with the server offset.");
                    }
                    offset = await UploadOffsetAsync(lease, endpoint, length, cancellationToken);
                    progress?.Invoke(offset);
                    continue;
                }
                await RequireSuccessAsync(request, response, cancellationToken);
                var next = Offset(response, length);
                if (next != offset + count)
                {
                    throw new InvalidDataException("The server acknowledged an unexpected upload offset.");
                }
                offset = next;
                retries = 0;
                progress?.Invoke(offset);
            }
            catch (Exception exception) when (++retries <= 4 && !cancellationToken.IsCancellationRequested && CanRetry(exception))
            {
                log?.Invoke($"Upload attempt {retries} failed for {Path.GetFileName(path)}. Retrying: {exception.Message}");
                await Task.Delay(TimeSpan.FromSeconds(retries), cancellationToken);
                offset = await UploadOffsetAsync(lease, endpoint, length, cancellationToken);
                progress?.Invoke(offset);
            }
        }
    }

    internal async Task<T> GetJsonAsync<T>(WorkerTransferLease lease, string endpoint, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, lease, endpoint);
        using var response = await SendHeadersAsync(request, cancellationToken);
        await RequireSuccessAsync(request, response, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(networkTimeout);
        return await response.Content.ReadFromJsonAsync<T>(WorkerProtocol.JsonOptions, timeout.Token)
            ?? throw new InvalidDataException("The server returned an empty Worker response.");
    }

    internal async Task<TResponse> PostJsonResponseAsync<TRequest, TResponse>(WorkerTransferLease lease, string endpoint,
        TRequest payload, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, lease, endpoint);
        request.Content = JsonContent.Create(payload, options: WorkerProtocol.JsonOptions);
        using var response = await SendHeadersAsync(request, cancellationToken);
        await RequireSuccessAsync(request, response, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(networkTimeout);
        return await response.Content.ReadFromJsonAsync<TResponse>(WorkerProtocol.JsonOptions, timeout.Token)
            ?? throw new InvalidDataException("The server returned an empty Worker acknowledgement.");
    }

    internal async Task PostJsonAsync<T>(WorkerTransferLease lease, string endpoint, T payload, CancellationToken cancellationToken,
        TimeSpan? timeout = null, int retries = 0, Action<string>? log = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = CreateRequest(HttpMethod.Post, lease, endpoint);
                request.Content = JsonContent.Create(payload, options: WorkerProtocol.JsonOptions);
                using var response = await SendHeadersAsync(request, cancellationToken, timeout);
                await RequireSuccessAsync(request, response, cancellationToken);
                return;
            }
            catch (Exception exception) when (attempt < retries && !cancellationToken.IsCancellationRequested && CanRetry(exception))
            {
                log?.Invoke($"The server has not confirmed {endpoint}. Retrying: {exception.Message}");
                await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancellationToken);
            }
        }
    }

    internal async Task RequireSuccessAsync(HttpRequestMessage request, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
            or HttpStatusCode.Gone or HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            throw new WorkerLeaseLostException("The server rejected the current job lease or worker credentials (status " + (int)response.StatusCode + ").");
        }
        string? detail = null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(networkTimeout);
        try
        {
            await response.Content.LoadIntoBufferAsync(MAX_ERROR_RESPONSE_BYTES, limit.Token);
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(limit.Token));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("detail", out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                detail = value.GetString();
            }
        }
        catch (Exception exception) when (exception is JsonException or HttpRequestException or IOException
                                          || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
        }
        cancellationToken.ThrowIfCancellationRequested();
        var message = FormattableString.Invariant($"The server rejected {request.Method} {request.RequestUri!.AbsolutePath} (status {(int)response.StatusCode} {response.ReasonPhrase}).");
        if (!string.IsNullOrWhiteSpace(detail))
        {
            message += " " + detail.Trim();
        }
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    internal static bool CanRetry(Exception exception)
    {
        if (exception is HttpRequestException { StatusCode: { } status })
        {
            return status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
        }
        return exception is HttpRequestException or IOException;
    }

    private async Task<HttpResponseMessage> SendUploadAsync(HttpRequestMessage request, CancellationToken transferToken,
        CancellationToken jobToken)
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

    private async Task<long> UploadOffsetAsync(WorkerTransferLease lease, string endpoint, long length, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Head, lease, endpoint);
        request.Headers.TryAddWithoutValidation("Upload-Length", length.ToString(CultureInfo.InvariantCulture));
        using var response = await SendHeadersAsync(request, cancellationToken);
        await RequireSuccessAsync(request, response, cancellationToken);
        return Offset(response, length);
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

    /// <inheritdoc />
    public void Dispose() => client.Dispose();
}
