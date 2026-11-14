using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;
using Xunit;

namespace Yohuke.LiveRecorder.AI.Tests;

public sealed class AiChatTransportTests
{
    private const string COMPLETE_RESPONSE = """{"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","reasoning_content":"已完成","tool_calls":[{"id":"c","type":"function","function":{"name":"complete","arguments":"{}"}}]}}],"usage":{"prompt_tokens":123,"completion_tokens":45}}""";

    [Fact]
    public void ServiceRegistrationResolvesTheClientWithItsClock()
    {
        var services = new ServiceCollection();
        var clock = new AiManualTimeProvider();
        services.AddSingleton<TimeProvider>(clock);
        services.AddLiveRecorderAi();
        using var provider = services.BuildServiceProvider();
        Assert.Same(clock, provider.GetRequiredService<TimeProvider>());
        Assert.IsType<OpenAiCompatibleChatClient>(provider.GetRequiredService<IAiChatClient>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedSuccessfulResponseIsClassifiedForRetry(bool partialBody)
    {
        var reads = 0;
        using var stream = new AiResponseReadStream((buffer, _) =>
        {
            if (partialBody && reads++ == 0)
            {
                return ValueTask.FromResult(Write(buffer, COMPLETE_RESPONSE[..^12]));
            }
            throw new HttpIOException(HttpRequestError.ResponseEnded, "private-provider-detail");
        });
        using var http = Client(stream);
        var error = await Assert.ThrowsAsync<AiProviderException>(() => CompleteAsync(http));
        Assert.Equal("AI_RESPONSE_INTERRUPTED", error.Code);
        Assert.True(error.UnknownOutcome);
        Assert.True(error.Retryable);
        Assert.Null(error.PromptTokens);
        Assert.DoesNotContain("private-provider-detail", error.ToString());
    }

    [Theory]
    [InlineData("io")]
    [InlineData("http")]
    [InlineData("timeout")]
    public async Task CompleteResponseSurvivesTailTransportFailure(string failure)
    {
        var reads = 0;
        using var stream = new AiResponseReadStream((buffer, _) =>
        {
            if (reads++ == 0)
            {
                return ValueTask.FromResult(Write(buffer, COMPLETE_RESPONSE));
            }
            throw failure switch
            {
                "io" => new HttpIOException(HttpRequestError.ResponseEnded),
                "http" => new HttpRequestException(),
                _ => new OperationCanceledException()
            };
        });
        using var http = Client(stream);
        var result = await CompleteAsync(http);
        Assert.Equal("complete", result.Message["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("已完成", result.Message["reasoning_content"]!.GetValue<string>());
        Assert.Equal(123, result.PromptTokens);
        Assert.Equal(45, result.CompletionTokens);
    }

    [Fact]
    public async Task TruncatedJsonAtEndOfStreamIsRetryable()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(COMPLETE_RESPONSE[..^1]));
        using var http = Client(stream);
        var error = await Assert.ThrowsAsync<AiProviderException>(() => CompleteAsync(http));
        Assert.Equal("AI_RESPONSE_INTERRUPTED", error.Code);
        Assert.True(error.Retryable);
    }

    [Fact]
    public async Task TailFailureRequiresAnExplicitSuccessfulFinishReason()
    {
        var json = JsonNode.Parse(COMPLETE_RESPONSE)!;
        json["choices"]![0]!.AsObject().Remove("finish_reason");
        var reads = 0;
        using var stream = new AiResponseReadStream((buffer, _) => reads++ == 0
            ? ValueTask.FromResult(Write(buffer, json.ToJsonString()))
            : throw new IOException());
        using var http = Client(stream);
        var error = await Assert.ThrowsAsync<AiProviderException>(() => CompleteAsync(http));
        Assert.Equal("AI_RESPONSE_INTERRUPTED", error.Code);
        Assert.True(error.Retryable);
        Assert.Equal(123, error.PromptTokens);
        Assert.Equal(45, error.CompletionTokens);
    }

    [Theory]
    [InlineData("length", "AI_OUTPUT_LIMIT_REACHED", false)]
    [InlineData("aborted", "AI_RESPONSE_INTERRUPTED", true)]
    [InlineData("insufficient_system_resource", "AI_RESPONSE_INTERRUPTED", true)]
    [InlineData("content_filter", "AI_RESPONSE_INVALID", false)]
    [InlineData("unexpected", "AI_RESPONSE_INVALID", false)]
    public async Task IncompleteFinishNeverReturnsExecutableTools(string finishReason, string code, bool retryable)
    {
        var json = JsonNode.Parse(COMPLETE_RESPONSE)!;
        json["choices"]![0]!["finish_reason"] = finishReason;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json.ToJsonString()));
        using var http = Client(stream);
        var error = await Assert.ThrowsAsync<AiProviderException>(() => CompleteAsync(http));
        Assert.Equal(code, error.Code);
        Assert.Equal(retryable, error.Retryable);
        Assert.Equal(123, error.PromptTokens);
        Assert.Equal(45, error.CompletionTokens);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("\"\"")]
    public async Task InvalidFinishReasonCannotBeTreatedAsAMissingCompatibilityField(string value)
    {
        var json = JsonNode.Parse(COMPLETE_RESPONSE)!;
        json["choices"]![0]!["finish_reason"] = JsonNode.Parse(value);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json.ToJsonString()));
        using var http = Client(stream);
        var error = await Assert.ThrowsAsync<AiProviderException>(() => CompleteAsync(http));
        Assert.Equal("AI_RESPONSE_INVALID", error.Code);
        Assert.False(error.Retryable);
        Assert.Equal(123, error.PromptTokens);
    }

    [Fact]
    public async Task KeepAliveResetsIdleTimeoutBeyondTheOldNinetySecondLimit()
    {
        var clock = new AiManualTimeProvider();
        var reads = 0;
        using var stream = new AiResponseReadStream((buffer, token) =>
        {
            clock.Advance(TimeSpan.FromSeconds(120));
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(reads++ switch
            {
                < 4 => Write(buffer, "\r\n"),
                4 => Write(buffer, COMPLETE_RESPONSE),
                _ => 0
            });
        });
        using var http = Client(stream);
        var result = await CompleteAsync(http, clock);
        Assert.Equal("tool_calls", result.FinishReason);
        Assert.True(clock.GetUtcNow() - DateTimeOffset.UnixEpoch > TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task IdleResponseTimesOutAndCanBeRetried()
    {
        var clock = new AiManualTimeProvider();
        using var stream = new AiResponseReadStream((_, token) =>
        {
            clock.Advance(TimeSpan.FromSeconds(181));
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(0);
        });
        using var http = Client(stream);
        var error = await Assert.ThrowsAsync<AiProviderException>(() => CompleteAsync(http, clock));
        Assert.Equal("AI_REQUEST_TIMEOUT", error.Code);
        Assert.True(error.UnknownOutcome);
        Assert.True(error.Retryable);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public async Task DisabledOrLargeTimeoutDoesNotOverflowThePlatformTimer(int seconds)
    {
        var clock = new AiManualTimeProvider();
        var reads = 0;
        using var stream = new AiResponseReadStream((buffer, token) =>
        {
            clock.Advance(TimeSpan.FromDays(60));
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(reads++ == 0 ? Write(buffer, COMPLETE_RESPONSE) : 0);
        });
        using var http = Client(stream);
        var result = await CompleteAsync(http, clock, seconds);
        Assert.Equal("tool_calls", result.FinishReason);
    }

    [Fact]
    public async Task CallerCancellationNeverSalvagesAResponseOrBecomesRetryable()
    {
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        using var stream = new AiResponseReadStream((buffer, token) =>
        {
            if (reads++ == 0)
            {
                return ValueTask.FromResult(Write(buffer, COMPLETE_RESPONSE));
            }
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(0);
        });
        using var http = Client(stream);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CompleteAsync(http, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task CallerCancellationDuringAnErrorBodyReadTakesPriorityOverTheHttpError()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new AiResponseReadStream((_, _) =>
        {
            cancellation.Cancel();
            throw new IOException();
        });
        using var http = new HttpClient(new AiTestHttpHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StreamContent(stream) })));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CompleteAsync(http, cancellationToken: cancellation.Token));
    }

    [Theory]
    [InlineData(408, true, false)]
    [InlineData(429, true, false)]
    [InlineData(500, true, true)]
    [InlineData(502, true, true)]
    [InlineData(503, true, true)]
    [InlineData(504, true, true)]
    [InlineData(400, false, false)]
    [InlineData(401, false, false)]
    [InlineData(403, false, false)]
    [InlineData(404, false, false)]
    public async Task HttpErrorsSeparateTransientRecoveryFromUnknownUsage(int status, bool retryable, bool unknown)
    {
        var requests = 0;
        using var http = new HttpClient(new AiTestHttpHandler(_ =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
        }));
        var error = await Assert.ThrowsAsync<AiProviderException>(() => CompleteAsync(http));
        Assert.Equal($"AI_HTTP_{status}", error.Code);
        Assert.Equal(retryable, error.Retryable);
        Assert.Equal(unknown, error.UnknownOutcome);
        Assert.Equal(1, requests);
    }

    private static int Write(Memory<byte> buffer, string value) => Encoding.UTF8.GetBytes(value, buffer.Span);

    private static HttpClient Client(Stream stream) => new(new AiTestHttpHandler(_ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })))
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static Task<AiCompletion> CompleteAsync(HttpClient http, TimeProvider? clock = null, int seconds = 180,
        CancellationToken cancellationToken = default) => new OpenAiCompatibleChatClient(http, clock ?? TimeProvider.System)
        .CompleteAsync(new AiSettings { BaseUrl = "http://localhost/v1", RequestTimeoutSeconds = seconds }, "", new(), new(), 2048, cancellationToken);
}
