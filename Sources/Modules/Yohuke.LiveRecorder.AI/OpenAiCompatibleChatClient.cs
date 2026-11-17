using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI;

/// <summary>支持 function calling 的有界 Chat Completions 客户端。</summary>
[method: ActivatorUtilitiesConstructor]
public sealed class OpenAiCompatibleChatClient(HttpClient http, TimeProvider timeProvider) : IAiChatClient
{
    private const int MAX_RESPONSE_BYTES = 64 * 1024 * 1024;
    private const int MAX_ERROR_BYTES = 16 * 1024;

    /// <summary>使用系统时钟创建客户端。</summary>
    public OpenAiCompatibleChatClient(HttpClient http) : this(http, TimeProvider.System)
    {
    }

    /// <inheritdoc />
    public async Task<AiCompletion> CompleteAsync(AiSettings settings, string apiKey, JsonArray messages,
        JsonArray tools, int maxOutputTokens, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["model"] = settings.Model,
            ["messages"] = messages.DeepClone(),
            ["tools"] = tools.DeepClone(),
            ["tool_choice"] = settings.Provider == "DeepSeek" && settings.ThinkingMode != "Disabled" ? "auto" : "required",
            ["stream"] = false,
            [settings.Provider == "OpenAI" ? "max_completion_tokens" : "max_tokens"] = maxOutputTokens
        };
        if (settings.Provider == "DeepSeek")
        {
            if (settings.ThinkingMode != "Default")
            {
                payload["thinking"] = new JsonObject { ["type"] = settings.ThinkingMode.ToLowerInvariant() };
            }
            if (settings.ThinkingMode != "Disabled" && settings.ReasoningEffort != "Default")
            {
                payload["reasoning_effort"] = settings.ReasoningEffort.ToLowerInvariant();
            }
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, AiAnalysisPolicy.Endpoint(settings.BaseUrl));
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var timeout = new AiRequestTimeout(timeProvider, settings.RequestTimeoutSeconds, cancellationToken);
        using var body = new MemoryStream();
        var responseLimit = Math.Min(MAX_RESPONSE_BYTES, Math.Max(262144L, (long)maxOutputTokens * 16 + 65536));
        var responseStarted = false;
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            cancellationToken.ThrowIfCancellationRequested();
            timeout.RecordActivity();
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var code = await IsContextRejectionAsync(response, timeout, cancellationToken)
                    ? "AI_CONTEXT_LENGTH_EXCEEDED" : $"AI_HTTP_{status}";
                throw new AiProviderException(code, unknownOutcome: status is >= 500 and <= 599,
                    retryable: status is 408 or 429 or 500 or 502 or 503 or 504);
            }
            responseStarted = true;
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[8192];
            int read;
            while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
            {
                timeout.RecordActivity();
                if (body.Length + read > responseLimit)
                {
                    throw new AiProviderException("AI_RESPONSE_TOO_LARGE", true);
                }
                body.Write(buffer, 0, read);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return ParseCompletion(body, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RecoverCompletedResponse(body, "AI_REQUEST_TIMEOUT", cancellationToken);
        }
        catch (HttpRequestException)
        {
            return RecoverCompletedResponse(body, responseStarted ? "AI_RESPONSE_INTERRUPTED" : "AI_NETWORK_UNKNOWN", cancellationToken);
        }
        catch (IOException)
        {
            return RecoverCompletedResponse(body, responseStarted ? "AI_RESPONSE_INTERRUPTED" : "AI_NETWORK_UNKNOWN", cancellationToken);
        }
        catch (JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new AiProviderException("AI_RESPONSE_INTERRUPTED", true, retryable: true);
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new AiProviderException("AI_RESPONSE_INVALID", true);
        }
    }

    private static AiCompletion RecoverCompletedResponse(MemoryStream body, string code, CancellationToken callerToken)
    {
        callerToken.ThrowIfCancellationRequested();
        try
        {
            var completion = ParseCompletion(body, true);
            callerToken.ThrowIfCancellationRequested();
            return completion;
        }
        catch (JsonException)
        {
            throw new AiProviderException(code, true, retryable: true);
        }
    }

    private static AiCompletion ParseCompletion(MemoryStream body, bool requireFinishReason)
    {
        var json = JsonNode.Parse(body.GetBuffer().AsSpan(0, checked((int)body.Length))) as JsonObject;
        var promptTokens = ReadUsage((json?["usage"] as JsonObject)?["prompt_tokens"]);
        var completionTokens = ReadUsage((json?["usage"] as JsonObject)?["completion_tokens"]);
        if (json?["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice)
        {
            throw new AiProviderException("AI_RESPONSE_INVALID", true, promptTokens, completionTokens);
        }
        if (choice.TryGetPropertyValue("finish_reason", out var finishValue) &&
            (finishValue is not JsonValue value || !value.TryGetValue<string>(out var reason) || reason.Length == 0))
        {
            throw new AiProviderException("AI_RESPONSE_INVALID", true, promptTokens, completionTokens);
        }
        var finishReason = ReadText(choice["finish_reason"]);
        if (finishReason == "length")
        {
            throw new AiProviderException("AI_OUTPUT_LIMIT_REACHED", true, promptTokens, completionTokens);
        }
        if (finishReason is "aborted" or "insufficient_system_resource" || requireFinishReason && finishReason.Length == 0)
        {
            throw new AiProviderException("AI_RESPONSE_INTERRUPTED", true, promptTokens, completionTokens, retryable: true);
        }
        if (finishReason is not ("" or "stop" or "tool_calls"))
        {
            throw new AiProviderException("AI_RESPONSE_INVALID", true, promptTokens, completionTokens);
        }
        if (choice["message"] is not JsonObject message || message["tool_calls"] is not JsonArray { Count: > 0 } calls)
        {
            throw new AiProviderException("AI_FUNCTION_CALL_REQUIRED", true, promptTokens, completionTokens);
        }
        foreach (var node in calls)
        {
            if (node is not JsonObject call || !IsBoundedText(call["id"], 200) ||
                call["function"] is not JsonObject function || !IsBoundedText(function["name"], 100) ||
                !IsBoundedText(function["arguments"], 32768))
            {
                throw new AiProviderException("AI_RESPONSE_INVALID", true, promptTokens, completionTokens);
            }
        }
        return new()
        {
            Message = (JsonObject)message.DeepClone(),
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            FinishReason = finishReason
        };
    }

    private static async Task<bool> IsContextRejectionAsync(HttpResponseMessage response, AiRequestTimeout timeout, CancellationToken callerToken)
    {
        if ((int)response.StatusCode is not (400 or 413 or 422) || response.Content.Headers.ContentLength > MAX_ERROR_BYTES)
        {
            return false;
        }
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var body = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                timeout.RecordActivity();
                if (body.Length + read > MAX_ERROR_BYTES)
                {
                    return false;
                }
                body.Write(buffer, 0, read);
            }
            if (JsonNode.Parse(body.ToArray()) is not JsonObject json || json["error"] is not JsonObject error)
            {
                return false;
            }
            var code = ReadText(error["code"]);
            var type = ReadText(error["type"]);
            if (code is "context_length_exceeded" or "context_window_exceeded" or "max_context_length_exceeded" or "prompt_too_long" or "input_too_long" ||
                type is "context_length_exceeded" or "context_window_exceeded" or "max_context_length_exceeded" or "prompt_too_long" or "input_too_long")
            {
                return true;
            }
            var message = ReadText(error["message"]);
            return message.Contains("maximum context length", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("context length exceeded", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("exceeds the context window", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is JsonException or IOException or HttpRequestException ||
                                      error is OperationCanceledException && !callerToken.IsCancellationRequested)
        {
            callerToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    private static string ReadText(JsonNode? value) =>
        value is JsonValue node && node.TryGetValue<string>(out var text) ? text : "";

    private static bool IsBoundedText(JsonNode? value, int maximum) =>
        value is JsonValue node && node.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text) && text.Length <= maximum;

    private static long? ReadUsage(JsonNode? value) =>
        value is JsonValue node && node.TryGetValue<long>(out var count) && count is >= 0 and <= int.MaxValue ? count : null;
}
