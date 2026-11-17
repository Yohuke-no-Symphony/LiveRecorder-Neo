using System.Text.Json;
using Yohuke.LiveRecorder.AI.Contracts;
using Yohuke.LiveRecorder.Domain;

namespace Yohuke.LiveRecorder.Application.AI;

/// <summary>隔离主站作业管理字段与节点可传输的模型执行状态。</summary>
public static class AiExecutionMapper
{
    private static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>建立不共享可变数据、不携带凭据和私有上下文路径的执行描述。</summary>
    public static AiExecutionState ToExecution(AiAnalysisJob job)
    {
        var context = Clone(job.Context);
        context.ConversationPath = null;
        var snapshot = Clone(job.Snapshot);
        snapshot.EvidenceRelativePath = "";
        return new()
        {
            JobId = job.Id, SessionId = job.SessionId, Purpose = job.Purpose, CreatedAtUtc = job.CreatedAtUtc,
            Settings = ToSettings(job.Settings), Snapshot = snapshot, Result = Clone(job.Result), Context = context,
            TokenBudget = job.TokenBudget, State = job.State, ErrorCode = job.ErrorCode, Progress = job.Progress,
            PromptTokens = job.PromptTokens, CompletionTokens = job.CompletionTokens, EstimatedTokens = job.EstimatedTokens,
            ReservedTokens = job.ReservedTokens, ModelRequests = job.ModelRequests, RequestRetries = job.RequestRetries,
            ToolCalls = job.ToolCalls, RequestInFlight = job.RequestInFlight
        };
    }

    /// <summary>恢复模型内核需要的作业，不创建主站所有者、调度身份或凭据。</summary>
    public static AiAnalysisJob ToJob(AiExecutionState execution) => new()
    {
        Id = execution.JobId, SessionId = execution.SessionId, Purpose = execution.Purpose, CreatedAtUtc = execution.CreatedAtUtc,
        Settings = ToSettings(execution.Settings), Snapshot = Clone(execution.Snapshot), Result = Clone(execution.Result),
        Context = Clone(execution.Context), TokenBudget = execution.TokenBudget, State = execution.State,
        ErrorCode = execution.ErrorCode, Progress = execution.Progress, PromptTokens = execution.PromptTokens,
        CompletionTokens = execution.CompletionTokens, EstimatedTokens = execution.EstimatedTokens,
        ReservedTokens = execution.ReservedTokens, ModelRequests = execution.ModelRequests, RequestRetries = execution.RequestRetries,
        ToolCalls = execution.ToolCalls, RequestInFlight = execution.RequestInFlight
    };

    /// <summary>仅投影模型参数，不传输主站保存的密钥与自动调度开关。</summary>
    public static AiExecutionSettings ToSettings(AiSettings settings) => new()
    {
        Provider = settings.Provider, BaseUrl = settings.BaseUrl, Model = settings.Model,
        TokensPerHour = settings.TokensPerHour, MinimumTokens = settings.MinimumTokens, MaximumTokens = settings.MaximumTokens,
        MaxOutputTokens = settings.MaxOutputTokens, ContextTokenTarget = settings.ContextTokenTarget,
        ContextWindowTokens = settings.ContextWindowTokens, MaxModelRequests = settings.MaxModelRequests,
        MaxToolCalls = settings.MaxToolCalls, RequestTimeoutSeconds = settings.RequestTimeoutSeconds,
        MaxRequestRetries = settings.MaxRequestRetries, ThinkingMode = settings.ThinkingMode, ReasoningEffort = settings.ReasoningEffort
    };

    /// <summary>为已获主站授权的内核执行建立无密钥参数。</summary>
    public static AiSettings ToSettings(AiExecutionSettings settings) => new()
    {
        Enabled = true, Provider = settings.Provider, BaseUrl = settings.BaseUrl, Model = settings.Model,
        TokensPerHour = settings.TokensPerHour, MinimumTokens = settings.MinimumTokens, MaximumTokens = settings.MaximumTokens,
        MaxOutputTokens = settings.MaxOutputTokens, ContextTokenTarget = settings.ContextTokenTarget,
        ContextWindowTokens = settings.ContextWindowTokens, MaxModelRequests = settings.MaxModelRequests,
        MaxToolCalls = settings.MaxToolCalls, RequestTimeoutSeconds = settings.RequestTimeoutSeconds,
        MaxRequestRetries = settings.MaxRequestRetries, ThinkingMode = settings.ThinkingMode, ReasoningEffort = settings.ReasoningEffort
    };

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), JsonOptions)
        ?? throw new InvalidDataException("The AI execution state contains an empty document.");
}
