using System.Text;
using System.Text.Json;
using Racka.Core.Tools;

namespace Racka.Core.Skills;

public sealed class LoadSkillTool(SkillCatalog catalog) : RackaTool(
    "load_skill",
    "Load the full instructions of a skill by name. Call this before working on a task that matches a skill from the Skills list.",
    """{"type":"object","properties":{"name":{"type":"string","description":"Skill name from the Skills list."}},"required":["name"]}""")
{
    public override Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        var name = RequireString(arguments, "name");
        var skill = catalog.Find(name);
        if (skill == null)
            return Task.FromResult(ToolResult.Error($"No skill named '{name}'. Available: {string.Join(", ", catalog.All.Select(s => s.Name))}"));

        var sb = new StringBuilder();
        sb.AppendLine($"# Skill: {skill.Name}");
        sb.AppendLine(skill.Body);
        var files = skill.ListFiles();
        if (files.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Files in this skill (read them with read_skill_file):");
            foreach (var f in files)
                sb.AppendLine($"- {f}");
        }
        return Task.FromResult(ToolResult.Ok(sb.ToString()));
    }
}

public sealed class ReadSkillFileTool(SkillCatalog catalog) : RackaTool(
    "read_skill_file",
    "Read a supporting file that belongs to a skill.",
    """{"type":"object","properties":{"name":{"type":"string","description":"Skill name."},"path":{"type":"string","description":"File path relative to the skill directory."}},"required":["name","path"]}""")
{
    private const int MaxChars = 100_000;

    public override Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        var name = RequireString(arguments, "name");
        var path = RequireString(arguments, "path");
        var skill = catalog.Find(name);
        if (skill == null)
            return Task.FromResult(ToolResult.Error($"No skill named '{name}'."));
        try
        {
            var text = skill.ReadFile(path);
            if (text.Length > MaxChars)
                text = text[..MaxChars] + $"\n…(truncated, file has {text.Length} characters)";
            return Task.FromResult(ToolResult.Ok(text));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ToolResult.Error(e.Message));
        }
    }
}
