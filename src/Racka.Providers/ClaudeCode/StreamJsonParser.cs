using System.Text.Json;

namespace Racka.Providers.ClaudeCode;

public abstract record StreamEvent;
public sealed record InitEvent(string SessionId, string? Model) : StreamEvent;
public sealed record AssistantTextEvent(string Text) : StreamEvent;
public sealed record ToolUseEvent(string Name) : StreamEvent;
public sealed record ResultEvent(bool IsError, string Subtype, string Result, string? SessionId,
    decimal? CostUsd, long InputTokens, long OutputTokens, int? NumTurns) : StreamEvent;

/// <summary>Parses the line-delimited JSON of `claude -p --output-format stream-json --verbose`.</summary>
public static class StreamJsonParser
{
    public static IEnumerable<StreamEvent> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
            yield break;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { yield break; }

        using (doc)
        {
            var root = doc.RootElement;
            switch (GetString(root, "type"))
            {
                case "system" when GetString(root, "subtype") == "init" && GetString(root, "session_id") is { } sid:
                    yield return new InitEvent(sid, GetString(root, "model"));
                    break;

                case "assistant" when root.TryGetProperty("message", out var message)
                                      && message.TryGetProperty("content", out var content)
                                      && content.ValueKind == JsonValueKind.Array:
                    foreach (var block in content.EnumerateArray())
                    {
                        var blockType = GetString(block, "type");
                        if (blockType == "text" && GetString(block, "text") is { Length: > 0 } text)
                            yield return new AssistantTextEvent(text);
                        else if (blockType == "tool_use" && GetString(block, "name") is { } name)
                            yield return new ToolUseEvent(name);
                    }
                    break;

                case "result":
                    long input = 0, output = 0;
                    if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        input = GetLong(usage, "input_tokens") + GetLong(usage, "cache_read_input_tokens") + GetLong(usage, "cache_creation_input_tokens");
                        output = GetLong(usage, "output_tokens");
                    }
                    var subtype = GetString(root, "subtype") ?? "";
                    var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True || subtype.StartsWith("error", StringComparison.Ordinal);
                    yield return new ResultEvent(
                        isError,
                        subtype,
                        GetString(root, "result") ?? "",
                        GetString(root, "session_id"),
                        root.TryGetProperty("total_cost_usd", out var cost) && cost.TryGetDecimal(out var c) ? c : null,
                        input,
                        output,
                        root.TryGetProperty("num_turns", out var turns) && turns.TryGetInt32(out var n) ? n : null);
                    break;
            }
        }
    }

    private static string? GetString(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long GetLong(JsonElement e, string property) =>
        e.TryGetProperty(property, out var v) && v.TryGetInt64(out var l) ? l : 0;
}
