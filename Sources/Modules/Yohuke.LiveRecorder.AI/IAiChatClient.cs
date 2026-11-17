using System.Text.Json.Nodes;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.AI;

/// <summary>与具体协议运输隔离的模型请求。</summary>
public interface IAiChatClient
{
    /// <summary>发出一次有输出上限的请求，不暗中重试。</summary>
    Task<AiCompletion> CompleteAsync(AiSettings settings, string apiKey, JsonArray messages,
        JsonArray tools, int maxOutputTokens, CancellationToken cancellationToken);
}
