namespace Racka.Core.Engine;

public static class TaskSources
{
    public const string Cli = "cli";
    public const string Bus = "bus";
    public const string Irc = "irc";
}

/// <param name="ConversationKey">Tasks with the same key continue the same conversation.</param>
public sealed record AgentTask(string Id, string Text, string Source, string? From, string ConversationKey)
{
    /// <summary>Agents that are waiting (via ask) on this task, outermost first. Used to break ask cycles.</summary>
    public IReadOnlyList<string> Chain { get; init; } = [];

    public static AgentTask Create(string text, string source, string? from = null, string? conversationKey = null) =>
        new(Guid.NewGuid().ToString("n")[..12], text, source, from, conversationKey ?? $"{source}:{from ?? "anonymous"}");
}

public sealed record AgentResult(string TaskId, bool Success, string Output, string? Error = null, AgentUsage? Usage = null)
{
    public static AgentResult Failed(string taskId, string error) => new(taskId, false, "", error);
}

public sealed record AgentUsage(long InputTokens, long OutputTokens, decimal? CostUsd = null);

public interface IAgentEngine : IAsyncDisposable
{
    Task<AgentResult> RunAsync(AgentTask task, CancellationToken ct);

    /// <summary>Forgets the history of a conversation; the next task with this key starts fresh.</summary>
    void ResetConversation(string conversationKey);
}
