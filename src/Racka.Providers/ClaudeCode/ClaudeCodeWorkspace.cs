using System.Text.Json;
using System.Text.Json.Nodes;
using Racka.Core.Agents;

namespace Racka.Providers.ClaudeCode;

/// <summary>The command Claude Code uses to start the Racka bridge MCP server.</summary>
public sealed record BridgeCommand(string Command, IReadOnlyList<string> Args);

/// <summary>Prepares an agent's work directory for Claude Code: MCP config and native skills.</summary>
public static class ClaudeCodeWorkspace
{
    public const string BridgeServerName = "racka";

    /// <summary>Writes state/mcp.json with the agent's MCP servers plus the Racka bridge. Returns null when empty.</summary>
    public static string? WriteMcpConfig(AgentDefinition agent, BridgeCommand? bridge)
    {
        var servers = new JsonObject();
        foreach (var (name, server) in agent.Config.McpServers.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            var entry = new JsonObject();
            if (server.IsStdio)
            {
                entry["command"] = server.Command;
                entry["args"] = new JsonArray(server.Args.Select(a => (JsonNode)a!).ToArray());
                if (server.Env.Count > 0)
                    entry["env"] = ToJsonObject(server.Env);
            }
            else
            {
                entry["type"] = "http";
                entry["url"] = server.Url;
                if (server.Headers.Count > 0)
                    entry["headers"] = ToJsonObject(server.Headers);
            }
            servers[name] = entry;
        }
        if (bridge != null)
        {
            servers[BridgeServerName] = new JsonObject
            {
                ["command"] = bridge.Command,
                ["args"] = new JsonArray(bridge.Args.Select(a => (JsonNode)a!).ToArray())
            };
        }
        if (servers.Count == 0)
            return null;

        Directory.CreateDirectory(agent.StateDirectory);
        var path = Path.Combine(agent.StateDirectory, "mcp.json");
        var json = new JsonObject { ["mcpServers"] = servers };
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>Symlinks the agent's skills into &lt;workdir&gt;/.claude/skills so Claude Code discovers them natively.</summary>
    public static IReadOnlyList<string> LinkSkills(AgentDefinition agent)
    {
        var warnings = new List<string>();
        if (agent.Skills.All.Count == 0)
            return warnings;

        var skillsDir = Path.Combine(agent.WorkDirectory, ".claude", "skills");
        Directory.CreateDirectory(skillsDir);
        foreach (var skill in agent.Skills.All)
        {
            var link = Path.Combine(skillsDir, skill.Name);
            var existing = new DirectoryInfo(link);
            if (existing.Exists || existing.LinkTarget != null)
            {
                if (existing.LinkTarget == null)
                {
                    warnings.Add($"Skill '{skill.Name}': {link} is a real directory, not replacing it.");
                    continue;
                }
                if (Path.GetFullPath(existing.LinkTarget, skillsDir) == skill.Directory)
                    continue;
                existing.Delete();
            }
            Directory.CreateSymbolicLink(link, skill.Directory);
        }
        return warnings;
    }

    private static JsonObject ToJsonObject(Dictionary<string, string> values)
    {
        var obj = new JsonObject();
        foreach (var (k, v) in values)
            obj[k] = v;
        return obj;
    }
}
