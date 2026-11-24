using System.Text;
using System.Text.Json;
using Racka.Core.Tools;

namespace Racka.Core.Memory;

public static class MemoryTools
{
    public static IReadOnlyList<IRackaTool> Create(MemoryStore store) =>
        [new MemorySaveTool(store), new MemoryReadTool(store), new MemorySearchTool(store), new MemoryDeleteTool(store)];
}

public sealed class MemorySaveTool(MemoryStore store) : RackaTool(
    "memory_save",
    "Save something worth remembering beyond this conversation, or replace an existing memory with the same name. " +
    "One memory holds one fact, preference, decision or lesson. Update an existing memory instead of creating a near-duplicate.",
    """
    {"type":"object","properties":{
      "name":{"type":"string","description":"Short kebab-case id, e.g. owner-prefers-hungarian. Reusing a name replaces that memory."},
      "description":{"type":"string","description":"One line that says what the memory is about; it is shown in your memory index."},
      "content":{"type":"string","description":"The memory itself in Markdown. Include why it matters and when it applies."}
    },"required":["name","description","content"]}
    """)
{
    public override Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        var name = RequireString(arguments, "name");
        try
        {
            var created = store.Save(name, RequireString(arguments, "description"), RequireString(arguments, "content"));
            return Task.FromResult(ToolResult.Ok(created ? $"Saved new memory '{name}'." : $"Updated memory '{name}'."));
        }
        catch (ArgumentException e)
        {
            return Task.FromResult(ToolResult.Error(e.Message));
        }
    }
}

public sealed class MemoryReadTool(MemoryStore store) : RackaTool(
    "memory_read",
    "Read a memory by name.",
    """{"type":"object","properties":{"name":{"type":"string","description":"Memory name from your memory index or memory_search."}},"required":["name"]}""")
{
    public override Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        var name = RequireString(arguments, "name");
        if (store.Get(name) is not { } entry)
            return Task.FromResult(ToolResult.Error($"No memory named '{name}'."));
        return Task.FromResult(ToolResult.Ok($"# {entry.Name}\n{entry.Description}\n(updated {entry.Updated:yyyy-MM-dd})\n\n{entry.Content}"));
    }
}

public sealed class MemorySearchTool(MemoryStore store) : RackaTool(
    "memory_search",
    "Search your memories by words. Without a query it lists every memory.",
    """
    {"type":"object","properties":{
      "query":{"type":"string","description":"Words to look for in names, descriptions and content."},
      "limit":{"type":"integer","description":"Maximum number of results (default 10)."}
    }}
    """)
{
    public override Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        var query = OptionalString(arguments, "query");
        var sb = new StringBuilder();
        if (string.IsNullOrWhiteSpace(query))
        {
            var all = store.List();
            if (all.Count == 0)
                return Task.FromResult(ToolResult.Ok("You have no memories yet."));
            foreach (var entry in all)
                sb.AppendLine($"- {entry.Name}: {entry.Description}");
            return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd()));
        }

        var hits = store.Search(query, Math.Clamp(OptionalInt(arguments, "limit", 10), 1, 50));
        if (hits.Count == 0)
            return Task.FromResult(ToolResult.Ok($"No memory matches '{query}'."));
        foreach (var hit in hits)
        {
            sb.AppendLine($"- {hit.Entry.Name}: {hit.Entry.Description}");
            if (hit.Snippet.Length > 0)
                sb.AppendLine($"  {hit.Snippet}");
        }
        return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd()));
    }
}

public sealed class MemoryDeleteTool(MemoryStore store) : RackaTool(
    "memory_delete",
    "Delete a memory that turned out to be wrong or is no longer relevant.",
    """{"type":"object","properties":{"name":{"type":"string","description":"Memory name."}},"required":["name"]}""")
{
    public override Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        var name = RequireString(arguments, "name");
        return Task.FromResult(store.Delete(name)
            ? ToolResult.Ok($"Deleted memory '{name}'.")
            : ToolResult.Error($"No memory named '{name}'."));
    }
}
