using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;

namespace Yohuke.LiveRecorder.Http.Tests;

internal sealed class RequestCryptographyProbe : IDisposable
{
    private readonly ActivityListener listener;
    private readonly EventHandler<FirstChanceExceptionEventArgs> onException;
    private int failures;
    private int serverActivities;
    internal ActivityTraceId TraceId { get; } = ActivityTraceId.CreateRandom();
    internal int CryptographicFailures => Volatile.Read(ref failures);
    internal bool ObservedServerActivity => Volatile.Read(ref serverActivities) > 0;

    internal RequestCryptographyProbe()
    {
        listener = new()
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                if (activity.TraceId == TraceId && activity.Kind == ActivityKind.Server)
                {
                    Interlocked.Increment(ref serverActivities);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        onException = (_, args) =>
        {
            if (args.Exception is CryptographicException && Activity.Current?.TraceId == TraceId)
            {
                Interlocked.Increment(ref failures);
            }
        };
        AppDomain.CurrentDomain.FirstChanceException += onException;
    }

    internal Task<HttpResponseMessage> GetAsync(HttpClient client, string path) =>
        SendAsync(client, HttpMethod.Get, path, null, null);

    internal Task<HttpResponseMessage> PostAsync(HttpBrowser browser, string path, object body) =>
        SendAsync(browser.Client, HttpMethod.Post, path, body, browser.CsrfToken);

    private async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body, string? csrfToken)
    {
        var parentId = $"00-{TraceId}-{ActivitySpanId.CreateRandom()}-01";
        using var parent = new Activity("foreign-cookie-probe").SetIdFormat(ActivityIdFormat.W3C).SetParentId(parentId).Start();
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        if (csrfToken is not null)
        {
            request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        }
        request.Headers.TryAddWithoutValidation("traceparent", parentId);
        return await client.SendAsync(request);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        AppDomain.CurrentDomain.FirstChanceException -= onException;
        listener.Dispose();
    }
}
