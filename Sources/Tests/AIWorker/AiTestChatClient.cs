using System.Text.Json.Nodes;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI.Worker.Tests;

internal sealed class AiTestChatClient : IAiChatClient
{
    internal int Calls { get; private set; }
    internal Func<Task<AiCompletion>>? Handler { get; set; }

    public Task<AiCompletion> CompleteAsync(AiSettings settings, string apiKey, JsonArray messages, JsonArray tools, int maxOutputTokens, CancellationToken cancellationToken)
    {
        Calls++;
        return Handler?.Invoke() ?? Task.FromResult(new AiCompletion { Message = new JsonObject { ["role"] = "assistant", ["content"] = "known response" }, PromptTokens = 4, CompletionTokens = 2 });
    }
}
