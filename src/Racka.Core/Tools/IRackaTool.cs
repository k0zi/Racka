using System.Text.Json;
using Racka.Core.Agents;
using Racka.Core.Engine;

namespace Racka.Core.Tools;

public interface IRackaTool
{
    string Name { get; }
    string Description { get; }
    /// <summary>JSON schema of the tool input (an object schema).</summary>
    JsonElement InputSchema { get; }
    Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext context, CancellationToken ct);
}

public sealed record ToolResult(string Content, bool IsError = false)
{
    public static ToolResult Ok(string content) => new(content);
    public static ToolResult Error(string message) => new(message, true);
}

public sealed record ToolContext(AgentDefinition Agent, AgentTask Task, IAgentEvents Events);

/// <summary>Convenience base for tools defined in code with a JSON schema literal.</summary>
public abstract class RackaTool : IRackaTool
{
    protected RackaTool(string name, string description, string inputSchemaJson)
    {
        Name = name;
        Description = description;
        using var doc = JsonDocument.Parse(inputSchemaJson);
        InputSchema = doc.RootElement.Clone();
    }

    public string Name { get; }
    public string Description { get; }
    public JsonElement InputSchema { get; }

    public abstract Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext context, CancellationToken ct);

    protected static string RequireString(JsonElement arguments, string property)
    {
        if (arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(value.GetString()))
            return value.GetString()!;
        throw new ToolArgumentException($"Missing required string argument '{property}'.");
    }

    protected static string? OptionalString(JsonElement arguments, string property) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    protected static bool OptionalBool(JsonElement arguments, string property, bool fallback) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(property, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : fallback;

    protected static int OptionalInt(JsonElement arguments, string property, int fallback) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(property, out var v) && v.TryGetInt32(out var i)
            ? i
            : fallback;
}

public sealed class ToolArgumentException(string message) : Exception(message);
