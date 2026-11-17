using System.Text.Json.Nodes;
using Yohuke.LiveRecorder.Application;
using Yohuke.LiveRecorder.Application.AI;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI;

/// <summary>仅以一个空参数函数检查 Chat Completions 工具调用能力。</summary>
public sealed class AiConnectionTester(IAiChatClient client) : IAiConnectionTester
{
    /// <inheritdoc />
    public async Task<AiConnectionTestResult> TestAsync(AiSettings settings, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            var tools = (JsonArray)JsonNode.Parse("""[{"type":"function","function":{"name":"connection_test","description":"Confirm tool support.","parameters":{"type":"object","properties":{},"additionalProperties":false}}}]""")!;
            var messages = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Call connection_test with empty arguments." });
            var outputLimit = settings.Provider == "DeepSeek" && settings.ThinkingMode != "Disabled"
                ? settings.MaxOutputTokens : Math.Min(256, settings.MaxOutputTokens);
            var response = await client.CompleteAsync(settings, apiKey, messages, tools, outputLimit, cancellationToken);
            if (response.Message["tool_calls"]?[0]?["function"]?["name"]?.GetValue<string>() != "connection_test")
            {
                throw AiErrors.CreateException("AI_FUNCTION_CALL_REQUIRED", 502);
            }
            return new(true, response.PromptTokens, response.CompletionTokens);
        }
        catch (AiProviderException error)
        {
            throw AiErrors.CreateException(error.Code, 502);
        }
    }
}
