using System.Text.Json;

namespace Racka.Core.Messaging;

public static class MessageKinds
{
    /// <summary>Fire and forget: the receiver queues the task and the sender does not wait.</summary>
    public const string Tell = "tell";
    /// <summary>Request/reply: the sender waits for the receiver's result.</summary>
    public const string Ask = "ask";
}

/// <param name="From">Sender agent name, or a free-form name for external callers (e.g. "cli:k0zi").</param>
/// <param name="Chain">Agents waiting on this message via ask, outermost first.</param>
public sealed record BusMessage(
    string Id,
    string From,
    string To,
    string Kind,
    string Text,
    string? ConversationKey = null,
    int? TimeoutSeconds = null,
    IReadOnlyList<string>? Chain = null,
    string Source = "bus");

public sealed record BusReply(string Id, bool Success, string Output, string? Error = null, bool Queued = false);

public sealed record AgentStatus(
    string Name,
    string Engine,
    string? Model,
    string State,
    string? CurrentTaskId,
    string? CurrentTaskFrom,
    int QueueLength,
    DateTimeOffset StartedAt,
    int ProcessId,
    bool IrcConnected,
    int CompletedTasks,
    int FailedTasks);

public sealed record ToolDescriptor(string Name, string Description, JsonElement InputSchema);

public sealed record ToolInvocation(string Name, JsonElement Arguments, string? TaskId = null);

public sealed record ToolInvocationResult(string Content, bool IsError);

public static class BusJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
