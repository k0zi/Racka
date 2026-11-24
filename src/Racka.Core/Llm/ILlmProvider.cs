using System.Text.Json;
using Racka.Core.Tools;

namespace Racka.Core.Llm;

/// <summary>
/// A chat model backend used by the API loop engine. Each provider keeps the conversation
/// in its own native message types; the history is append-only.
/// </summary>
public interface ILlmProvider
{
    IConversation CreateConversation(string systemPrompt);

    /// <summary>
    /// Sends the conversation, appends the assistant reply to it on success and returns the turn.
    /// Must not modify the conversation when it throws, so the call can be retried.
    /// </summary>
    Task<LlmTurn> NextTurnAsync(IConversation conversation, IReadOnlyList<IRackaTool> tools, CancellationToken ct);
}

public interface IConversation
{
    void AddUserText(string text);
    void AddToolResults(IReadOnlyList<ToolCallResult> results);
}

public sealed record ToolCall(string Id, string Name, JsonElement Arguments);

public sealed record ToolCallResult(ToolCall Call, ToolResult Result);

public sealed record LlmUsage(long InputTokens, long OutputTokens);

public sealed record LlmTurn(string Text, IReadOnlyList<ToolCall> ToolCalls, string StopReason, LlmUsage? Usage = null)
{
    public bool HasToolCalls => ToolCalls.Count > 0;
}

public sealed class LlmRefusalException(string message) : Exception(message);
