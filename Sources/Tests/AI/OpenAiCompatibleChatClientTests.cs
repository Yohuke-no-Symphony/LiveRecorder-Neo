using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Yohuke.LiveRecorder.Domain;
using Xunit;

namespace Yohuke.LiveRecorder.AI.Tests;

public sealed class OpenAiCompatibleChatClientTests
{
    [Theory]
    [InlineData("OpenAI", "max_completion_tokens")]
    [InlineData("DeepSeek", "max_tokens")]
    [InlineData("Custom", "max_tokens")]
    public async Task SendsToolProtocolAndOutputLimitWithoutLeakingKeyIntoBody(string provider, string limitName)
    {
        using var http = new HttpClient(new AiTestHttpHandler(async request =>
        {
            Assert.Equal("http://localhost:9999/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer test-secret", request.Headers.Authorization!.ToString());
            var text = await request.Content!.ReadAsStringAsync();
            Assert.DoesNotContain("test-secret", text);
            var body = JsonNode.Parse(text)!;
            Assert.Equal(provider == "DeepSeek" ? 2048 : 256, body[limitName]!.GetValue<int>());
            Assert.Equal(provider == "DeepSeek" ? "auto" : "required", body["tool_choice"]!.GetValue<string>());
            return Response("""{"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","tool_calls":[{"id":"c","type":"function","function":{"name":"connection_test","arguments":"{}"}}]}}],"usage":{"prompt_tokens":12,"completion_tokens":8}}""");
        }));
        var result = await new AiConnectionTester(new OpenAiCompatibleChatClient(http)).TestAsync(new() { Provider = provider, BaseUrl = "http://localhost:9999/v1/", Model = "mock" }, "test-secret", CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(12, result.PromptTokens);
    }

    [Fact]
    public async Task RejectsAProviderWithoutFunctionCalling()
    {
        using var http = new HttpClient(new AiTestHttpHandler(_ => Task.FromResult(Response("""{"choices":[{"message":{"content":"hello"}}]}"""))));
        var error = await Assert.ThrowsAsync<AiProviderException>(() => new OpenAiCompatibleChatClient(http)
            .CompleteAsync(new() { BaseUrl = "http://localhost/v1" }, "", new(), new(), 256, CancellationToken.None));
        Assert.Equal("AI_FUNCTION_CALL_REQUIRED", error.Code);
    }

    /// <summary>DeepSeek 思考模式使用自动工具选择，显式关闭才允许强制工具调用。</summary>
    [Theory]
    [InlineData("Default", "Default", "auto", null, null)]
    [InlineData("Enabled", "High", "auto", "enabled", "high")]
    [InlineData("Enabled", "Max", "auto", "enabled", "max")]
    [InlineData("Default", "Low", "auto", null, "low")]
    [InlineData("Disabled", "High", "required", "disabled", null)]
    public async Task DeepSeekThinkingUsesCompatibleToolChoice(string mode, string effort, string choice, string? thinking, string? sentEffort)
    {
        using var http = new HttpClient(new AiTestHttpHandler(async request =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Assert.Equal(choice, body["tool_choice"]!.GetValue<string>());
            Assert.Equal(thinking, body["thinking"]?["type"]?.GetValue<string>());
            Assert.Equal(sentEffort, body["reasoning_effort"]?.GetValue<string>());
            Assert.Equal(4096, body["max_tokens"]!.GetValue<int>());
            Assert.Equal("prior-reasoning", body["messages"]![0]!["reasoning_content"]!.GetValue<string>());
            return Response("""{"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","reasoning_content":"next-reasoning","tool_calls":[{"id":"c","type":"function","function":{"name":"connection_test","arguments":"{}"}}]}}]}""");
        }));
        var messages = new JsonArray(new JsonObject { ["role"] = "assistant", ["content"] = null, ["reasoning_content"] = "prior-reasoning" });
        var result = await new OpenAiCompatibleChatClient(http).CompleteAsync(new()
        {
            Provider = "DeepSeek", BaseUrl = "http://localhost/v1", Model = "deepseek-flash", ThinkingMode = mode, ReasoningEffort = effort
        }, "", messages, new(), 4096, CancellationToken.None);
        Assert.Equal("next-reasoning", result.Message["reasoning_content"]!.GetValue<string>());
    }

    /// <summary>其他兼容服务不接收 DeepSeek 专属思考参数。</summary>
    [Theory]
    [InlineData("OpenAI")]
    [InlineData("Custom")]
    public async Task OtherProvidersDoNotReceiveDeepSeekParameters(string provider)
    {
        using var http = new HttpClient(new AiTestHttpHandler(async request =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Assert.Null(body["thinking"]);
            Assert.Null(body["reasoning_effort"]);
            return Response("""{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"c","function":{"name":"connection_test","arguments":"{}"}}]}}]}""");
        }));
        await new OpenAiCompatibleChatClient(http).CompleteAsync(new()
        {
            Provider = provider, BaseUrl = "http://localhost/v1", ThinkingMode = "Enabled", ReasoningEffort = "Max"
        }, "", new(), new(), 1024, CancellationToken.None);
    }

    /// <summary>思考耗尽输出额度时报告额度不足，不能误报为模型不支持工具。</summary>
    [Fact]
    public async Task ExhaustedReasoningOutputReportsItsLimit()
    {
        using var http = new HttpClient(new AiTestHttpHandler(_ => Task.FromResult(Response("""{"choices":[{"finish_reason":"length","message":{"role":"assistant","content":null,"reasoning_content":"unfinished"}}]}"""))));
        var error = await Assert.ThrowsAsync<AiProviderException>(() => new OpenAiCompatibleChatClient(http)
            .CompleteAsync(new() { Provider = "DeepSeek", BaseUrl = "http://localhost/v1" }, "", new(), new(), 512, CancellationToken.None));
        Assert.Equal("AI_OUTPUT_LIMIT_REACHED", error.Code);
        Assert.True(error.UnknownOutcome);
    }

    /// <summary>百万 token 配置不会继续受原先 256 KiB 响应窗口限制。</summary>
    [Fact]
    public async Task LargeConfiguredOutputAcceptsLongReasoningResponses()
    {
        var reasoning = new string('x', 300000);
        using var http = new HttpClient(new AiTestHttpHandler(async request =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Assert.Equal(1000000, body["max_tokens"]!.GetValue<int>());
            return Response($$$"""{"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","reasoning_content":"{{{reasoning}}}","tool_calls":[{"id":"c","function":{"name":"connection_test","arguments":"{}"}}]}}]}""");
        }));
        var result = await new OpenAiCompatibleChatClient(http).CompleteAsync(new()
        {
            Provider = "Custom", BaseUrl = "http://localhost/v1", Model = "mock"
        }, "", new(), new(), 1000000, CancellationToken.None);
        Assert.Equal(reasoning.Length, result.Message["reasoning_content"]!.GetValue<string>().Length);
    }

    [Fact]
    public async Task HttpFailureDoesNotExposeResponseSecretsOrRetry()
    {
        var calls = 0;
        using var http = new HttpClient(new AiTestHttpHandler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("secret-key-from-provider") });
        }));
        var error = await Assert.ThrowsAsync<AiProviderException>(() => new OpenAiCompatibleChatClient(http)
            .CompleteAsync(new() { BaseUrl = "http://localhost/v1" }, "secret-key", new(), new(), 256, CancellationToken.None));
        Assert.Equal("AI_HTTP_401", error.Message);
        Assert.Equal(1, calls);
    }

    /// <summary>明确的上下文拒绝可供运行器压缩重试，但不泄露服务端正文。</summary>
    [Theory]
    [InlineData(400, "{\"error\":{\"code\":\"context_length_exceeded\",\"message\":\"private-provider-detail\"}}")]
    [InlineData(413, "{\"error\":{\"type\":\"input_too_long\"}}")]
    [InlineData(422, "{\"error\":{\"code\":\"invalid_request_error\",\"message\":\"This model's maximum context length is 65536 tokens. private-provider-detail\"}}")]
    public async Task ExplicitContextRejectionCanBeCompacted(int status, string body)
    {
        var calls = 0;
        using var http = new HttpClient(new AiTestHttpHandler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });
        }));
        var error = await Assert.ThrowsAsync<AiProviderException>(() => new OpenAiCompatibleChatClient(http)
            .CompleteAsync(new() { BaseUrl = "http://localhost/v1" }, "", new(), new(), 256, CancellationToken.None));
        Assert.Equal("AI_CONTEXT_LENGTH_EXCEEDED", error.Code);
        Assert.False(error.UnknownOutcome);
        Assert.DoesNotContain("private-provider-detail", error.ToString());
        Assert.Equal(1, calls);
    }

    /// <summary>授权、普通参数错误和不可解析响应不得误判为可重试的上下文拒绝。</summary>
    [Theory]
    [InlineData(401, "{\"error\":{\"code\":\"context_length_exceeded\"}}")]
    [InlineData(400, "{\"error\":{\"code\":\"invalid_request_error\",\"message\":\"max_tokens must be less than 393216\"}}")]
    [InlineData(400, "{\"error\":{\"message\":\"reasoning_content is missing\"}}")]
    [InlineData(400, "{\"error\":{\"message\":[\"context_length_exceeded\"]}}")]
    [InlineData(400, "context_length_exceeded private-provider-detail")]
    public async Task OtherHttpErrorsRemainNonRetryable(int status, string body)
    {
        using var http = new HttpClient(new AiTestHttpHandler(_ => Task.FromResult(
            new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) })));
        var error = await Assert.ThrowsAsync<AiProviderException>(() => new OpenAiCompatibleChatClient(http)
            .CompleteAsync(new() { BaseUrl = "http://localhost/v1" }, "", new(), new(), 256, CancellationToken.None));
        Assert.Equal($"AI_HTTP_{status}", error.Code);
        Assert.False(error.UnknownOutcome);
    }

    /// <summary>错误正文读取有界，过大正文不会被解析或暴露。</summary>
    [Fact]
    public async Task LargeHttpErrorIsNotParsedForRetry()
    {
        using var http = new HttpClient(new AiTestHttpHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(new JsonObject
                {
                    ["error"] = new JsonObject { ["code"] = "context_length_exceeded", ["message"] = new string('x', 20000) }
                }.ToJsonString())
            })));
        var error = await Assert.ThrowsAsync<AiProviderException>(() => new OpenAiCompatibleChatClient(http)
            .CompleteAsync(new() { BaseUrl = "http://localhost/v1" }, "", new(), new(), 256, CancellationToken.None));
        Assert.Equal("AI_HTTP_400", error.Code);
    }

    /// <summary>响应生成失败但有真实用量时，调用方可以按实际值记账。</summary>
    [Theory]
    [InlineData("length", "AI_OUTPUT_LIMIT_REACHED")]
    [InlineData("stop", "AI_FUNCTION_CALL_REQUIRED")]
    public async Task IncompleteOutputPreservesReportedUsage(string finishReason, string expectedCode)
    {
        var json = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["finish_reason"] = finishReason,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "incomplete" }
            }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 1234, ["completion_tokens"] = 321 }
        };
        using var http = new HttpClient(new AiTestHttpHandler(_ => Task.FromResult(Response(json.ToJsonString()))));
        var error = await Assert.ThrowsAsync<AiProviderException>(() => new OpenAiCompatibleChatClient(http)
            .CompleteAsync(new() { BaseUrl = "http://localhost/v1" }, "", new(), new(), 2048, CancellationToken.None));
        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(1234, error.PromptTokens);
        Assert.Equal(321, error.CompletionTokens);
    }

    /// <summary>已知拒绝后的错误正文故障不会改判为可能计费的未知请求。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedRequestRemainsRejectedWhenErrorBodyCannotBeRead(bool cancelledRead)
    {
        using var http = new HttpClient(new AiTestHttpHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new AiFaultedHttpContent(cancelledRead ? new OperationCanceledException() : new IOException("private-detail"))
            })));
        var error = await Assert.ThrowsAsync<AiProviderException>(() => new OpenAiCompatibleChatClient(http)
            .CompleteAsync(new() { BaseUrl = "http://localhost/v1" }, "", new(), new(), 2048, CancellationToken.None));
        Assert.Equal("AI_HTTP_400", error.Code);
        Assert.False(error.UnknownOutcome);
        Assert.DoesNotContain("private-detail", error.ToString());
    }

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
