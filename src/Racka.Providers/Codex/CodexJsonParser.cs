using System.Text.Json;

namespace Racka.Providers.Codex;

public abstract record CodexEvent;

public sealed record ThreadStartedEvent(string ThreadId) : CodexEvent;

/// <summary>A shell command, MCP call, file change or web search, reported once per item id.</summary>
public sealed record CodexToolEvent(string ItemId, string Description) : CodexEvent;

public sealed record AgentMessageEvent(string Text) : CodexEvent;

public sealed record TurnCompletedEvent(long InputTokens, long CachedInputTokens, long OutputTokens) : CodexEvent;

public sealed record CodexErrorEvent(string Message, bool TurnFailed) : CodexEvent;

/// <summary>Parses the JSONL that `codex exec --json` prints; unknown or malformed lines are skipped.</summary>
public static class CodexJsonParser
{
    public static IEnumerable<CodexEvent> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
            return [];
        try
        {
            using var doc = JsonDocument.Parse(line);
            return ParseEvent(doc.RootElement) is { } e ? [e] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static CodexEvent? ParseEvent(JsonElement root)
    {
        switch (Str(root, "type"))
        {
            case "thread.started":
                return Str(root, "thread_id") is { } id ? new ThreadStartedEvent(id) : null;
            case "turn.completed":
                var usage = root.TryGetProperty("usage", out var u) ? u : default;
                return new TurnCompletedEvent(Num(usage, "input_tokens"), Num(usage, "cached_input_tokens"), Num(usage, "output_tokens"));
            case "turn.failed":
                var error = root.TryGetProperty("error", out var err) ? Str(err, "message") : null;
                return new CodexErrorEvent(error ?? "turn failed", true);
            case "error":
                return new CodexErrorEvent(Str(root, "message") ?? "unknown error", false);
            case "item.started":
            case "item.completed":
                return root.TryGetProperty("item", out var item) ? ParseItem(item, Str(root, "type") == "item.completed") : null;
            default:
                return null;
        }
    }

    private static CodexEvent? ParseItem(JsonElement item, bool completed)
    {
        var id = Str(item, "id") ?? "";
        switch (Str(item, "type"))
        {
            case "agent_message" when completed:
                return Str(item, "text") is { } text ? new AgentMessageEvent(text) : null;
            case "command_execution":
                return new CodexToolEvent(id, "shell: " + (Str(item, "command") ?? "?"));
            case "mcp_tool_call":
                return new CodexToolEvent(id, $"{Str(item, "server")}.{Str(item, "tool")}");
            case "file_change":
                var paths = item.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", changes.EnumerateArray().Select(c => Str(c, "path")).Where(p => p != null))
                    : "";
                return new CodexToolEvent(id, "edit: " + paths);
            case "web_search":
                return new CodexToolEvent(id, "web_search: " + Str(item, "query"));
            case "error" when completed:
                return new CodexErrorEvent(Str(item, "message") ?? "error", false);
            default:
                return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
}
