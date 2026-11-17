using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Yohuke.LiveRecorder.Recording.Contracts;
using Yohuke.LiveRecorder.Transcoding.Contracts;

namespace Yohuke.LiveRecorder.Recording.Worker.Tests;

internal sealed class RecordingUploadLogTestHandler : HttpMessageHandler
{
    private int registrations;
    private int segmentCommits;
    private int eventCommits;

    internal bool RejectRegistration { get; init; }
    internal HttpStatusCode? RejectedSegmentCommitStatus { get; init; }
    internal int Registrations => Volatile.Read(ref registrations);
    internal int SegmentCommits => Volatile.Read(ref segmentCommits);
    internal int EventCommits => Volatile.Read(ref eventCommits);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/sessions", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref registrations);
            return new(RejectRegistration ? HttpStatusCode.Forbidden : HttpStatusCode.NoContent);
        }
        if (request.Method == HttpMethod.Head || request.Method == HttpMethod.Put)
        {
            var response = new HttpResponseMessage(HttpStatusCode.NoContent);
            var length = request.Method == HttpMethod.Head ? "0" : request.Headers.GetValues("Upload-Length").Single();
            if (request.Content is not null)
            {
                await request.Content.CopyToAsync(Stream.Null, cancellationToken);
            }
            response.Headers.Add("Upload-Offset", long.Parse(length, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
            return response;
        }
        if (path.EndsWith("/events/commit", StringComparison.Ordinal))
        {
            var commit = await request.Content!.ReadFromJsonAsync<RecordingEventCommit>(WorkerProtocol.JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("Missing event commit.");
            Interlocked.Increment(ref eventCommits);
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new RecordingEventAcknowledgement
                {
                    ThroughSequence = commit.ThroughSequence, CommittedBytes = commit.CommittedBytes,
                    IndexCommittedBytes = commit.IndexCommittedBytes, IsClosed = commit.IsClosed
                }, options: WorkerProtocol.JsonOptions)
            };
        }
        if (path.EndsWith("/commit", StringComparison.Ordinal))
        {
            var commit = await request.Content!.ReadFromJsonAsync<RecordingSegmentCommit>(WorkerProtocol.JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("Missing media commit.");
            Interlocked.Increment(ref segmentCommits);
            if (RejectedSegmentCommitStatus is { } status)
            {
                return new(status)
                {
                    Content = JsonContent.Create(new { detail = "The recording segment commit was rejected." })
                };
            }
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new RecordingCommitAcknowledgement
                {
                    CommitSequence = commit.CommitSequence, MediaCommittedBytes = commit.MediaCommittedBytes,
                    IndexCommittedBytes = commit.IndexCommittedBytes, IsClosed = commit.IsClosed
                }, options: WorkerProtocol.JsonOptions)
            };
        }
        throw new InvalidOperationException("Unexpected recording upload endpoint: " + path);
    }
}
