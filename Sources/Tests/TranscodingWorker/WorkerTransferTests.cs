using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Yohuke.LiveRecorder.Transcoding.Contracts;
using Yohuke.LiveRecorder.Transcoding.Worker.Configuration;
using Yohuke.LiveRecorder.Transcoding.Worker.Transport;
using Xunit;

namespace Yohuke.LiveRecorder.Transcoding.Worker.Tests;

/// <summary>验证受租约保护的文件续传与无进展超时。</summary>
public sealed class WorkerTransferTests
{
    /// <summary>输入按版本续传，每次复用缓存前重新向主站校验。</summary>
    [Fact]
    public async Task ResumesInputWithAuthenticatedVersionAndRevalidatesCacheReuse()
    {
        var directory = Path.Combine(Path.GetTempPath(), "worker-transfer-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var bytes = Enumerable.Range(0, 1000).Select(value => (byte)value).ToArray();
            var path = Path.Combine(directory, "input.flv");
            await File.WriteAllBytesAsync(path + ".partial", bytes[..400]);
            var assignment = Assignment(bytes.Length);
            var headCount = 0;
            var getCount = 0;
            var metrics = new List<WorkerTransferProgress>();
            using var transfer = new WorkerFileTransfer(Options(), new WorkerTestHttpHandler((request, _) =>
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("test-secret", request.Headers.Authorization?.Parameter);
                Assert.Equal(assignment.LeaseToken, request.Headers.GetValues("X-Transcode-Lease").Single());
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.ETag = new(assignment.SourceETag);
                if (request.Method == HttpMethod.Head)
                {
                    headCount++;
                    response.Content = new ByteArrayContent([]);
                    response.Content.Headers.ContentLength = bytes.Length;
                }
                else
                {
                    getCount++;
                    Assert.Equal(400, request.Headers.Range?.Ranges.Single().From);
                    Assert.Equal(assignment.SourceETag, request.Headers.IfRange?.EntityTag?.ToString());
                    response.StatusCode = HttpStatusCode.PartialContent;
                    response.Content = new ByteArrayContent(bytes[400..]);
                    response.Content.Headers.ContentRange = new(400, bytes.Length - 1, bytes.Length);
                }
                return Task.FromResult(response);
            }));
            await transfer.DownloadAsync(assignment, path, 0, _ => { }, CancellationToken.None, metrics.Add);
            Assert.Equal(400, metrics[0].BytesTransferred);
            Assert.Equal(1000, metrics[^1].BytesTransferred);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            await transfer.DownloadAsync(assignment, path, 0, _ => { }, CancellationToken.None);
            Assert.Equal(2, headCount);
            Assert.Equal(1, getCount);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>丢失提交响应后依据主站偏移恢复，避免重发已写入块。</summary>
    [Fact]
    public async Task LostUploadResponseResumesFromServerOffsetWithoutResendingCommittedBlock()
    {
        var directory = Path.Combine(Path.GetTempPath(), "worker-upload-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var bytes = RandomNumberGenerator.GetBytes(8 * 1024 * 1024 + 123);
            await File.WriteAllBytesAsync(Path.Combine(directory, "chunk_00000.m4s"), bytes);
            var manifest = new TranscodeOutputManifest { Files = [new() { Name = "chunk_00000.m4s", Length = bytes.Length }] };
            using var received = new MemoryStream();
            var offsets = new List<long>();
            var first = true;
            var completed = false;
            var metrics = new List<WorkerTransferProgress>();
            using var transfer = new WorkerFileTransfer(Options(), new WorkerTestHttpHandler(async (request, token) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    completed = true;
                    return new(HttpStatusCode.OK);
                }
                if (request.Method == HttpMethod.Put)
                {
                    offsets.Add(long.Parse(request.Headers.GetValues("Upload-Offset").Single()));
                    await request.Content!.CopyToAsync(received, token);
                    Assert.Equal(received.Length, metrics[^1].BytesTransferred);
                    if (first)
                    {
                        first = false;
                        throw new HttpRequestException("模拟已提交数据后丢失响应");
                    }
                }
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Add("Upload-Offset", received.Length.ToString());
                return response;
            }));
            await transfer.UploadAsync(Assignment(bytes.Length), directory, manifest, 0, _ => { }, CancellationToken.None, metrics.Add);
            Assert.Equal(new long[] { 0, 8 * 1024 * 1024 }, offsets);
            Assert.Equal(bytes, received.ToArray());
            Assert.True(completed);
            Assert.Equal(bytes.Length, metrics[^1].BytesTransferred);
            Assert.Equal(1, metrics[^1].FilesCompleted);
            Assert.Contains(metrics, value => value.BytesTransferred > 0 && value.BytesTransferred < bytes.Length);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>租约冲突时停止，不继续下载输入。</summary>
    [Fact]
    public async Task LeaseConflictOnInputStopsBeforeDownloading()
    {
        using var transfer = new WorkerFileTransfer(Options(), new WorkerTestHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict))));
        await Assert.ThrowsAsync<WorkerLeaseLostException>(() => transfer.DownloadAsync(Assignment(10), "/unused", 0, _ => { }, CancellationToken.None));
    }

    private static WorkerConnectionOptions Options() => new() { ApiBaseUrl = "https://worker.test", WorkerId = "test-worker", Secret = "test-secret" };

    /// <summary>即使任务仍有租约，响应头停滞也必须超时。</summary>
    [Fact]
    public async Task MissingResponseHeadersTimeoutEvenWhileJobLeaseRemainsValid()
    {
        using var transfer = new WorkerFileTransfer(Options(), new WorkerTestHttpHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(HttpStatusCode.OK);
        }), TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<IOException>(() => transfer.DownloadAsync(Assignment(10), "/unused", 0, _ => { }, CancellationToken.None));
    }

    /// <summary>主动限速等待不计作网络无进展，停止传输后仍会超时。</summary>
    [Fact]
    public async Task UploadThrottleDoesNotCountAsNetworkStall()
    {
        using var timeout = new TransferProgressTimeout(TimeSpan.FromMilliseconds(50), CancellationToken.None);
        using var source = new MemoryStream(new byte[100]);
        using var content = new UploadChunkContent(source, source.Length, new(), 500, timeout);
        using var output = new MemoryStream();
        await content.CopyToAsync(output, timeout.Token);
        Assert.Equal(100, output.Length);
        Assert.False(timeout.Token.IsCancellationRequested);
        await Task.Delay(100);
        Assert.True(timeout.Token.IsCancellationRequested);
    }

    private static TranscodeAssignment Assignment(long length) => new()
    {
        JobId = "test-job", Attempt = 2, LeaseToken = "test-lease", SourceLength = length, SourceETag = "\"version\""
    };
}
