using Racka.Core.Agents;
using Racka.Core.Config;
using Racka.Core.Engine;
using Racka.Core.Skills;
using Racka.Providers.ClaudeCode;

namespace Racka.Tests;

public class ClaudeCodeEngineTests
{
    private static readonly PromptOptions Options = new(false, true, false);

    // A stand-in for the claude CLI: records its arguments and stdin, then prints stream-json.
    private const string FakeClaude = """
        #!/usr/bin/env bash
        dir="$(dirname "$0")"
        printf '%s\n' "$@" > "$dir/args.$$"
        cat > "$dir/stdin.$$"
        ls "$dir"/args.* | wc -l > /dev/null
        for a in "$@"; do
          if [ "$prev" = "--resume" ] && [ "$a" = "stale-session" ]; then
            echo "No conversation found with session ID: stale-session" >&2
            exit 1
          fi
          prev="$a"
        done
        echo '{"type":"system","subtype":"init","session_id":"sess-1","model":"claude-opus-5-5"}'
        echo '{"type":"assistant","message":{"content":[{"type":"text","text":"Working on it"},{"type":"tool_use","name":"Read","input":{}}]}}'
        echo '{"type":"result","subtype":"success","is_error":false,"result":"Final answer","session_id":"sess-1","total_cost_usd":0.0123,"num_turns":2,"usage":{"input_tokens":100,"cache_read_input_tokens":50,"output_tokens":20}}'
        """;

    private static (AgentDefinition Agent, TempDir Dir) CreateAgent(Action<AgentConfig>? configure = null)
    {
        var dir = new TempDir();
        var script = dir.Write("bin/claude", FakeClaude);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var config = new AgentConfig { Name = "dolly", Engine = "claude-code", Model = "claude-opus-5-5" };
        config.ClaudeCode.Executable = script;
        configure?.Invoke(config);
        var agent = new AgentDefinition
        {
            Name = "dolly",
            Directory = Path.Combine(dir.Path, "agent"),
            Config = config,
            Soul = "You are Dolly.",
            Skills = new SkillCatalog([])
        };
        return (agent, dir);
    }

    private static List<string[]> RecordedArgs(TempDir dir) =>
        Directory.GetFiles(Path.Combine(dir.Path, "bin"), "args.*")
            .OrderBy(File.GetLastWriteTimeUtc)
            .Select(File.ReadAllLines)
            .ToList();

    [Fact]
    public void BuildsArguments()
    {
        var (agent, dir) = CreateAgent(c =>
        {
            c.Effort = "high";
            c.ClaudeCode.PermissionMode = "acceptEdits";
            c.ClaudeCode.MaxTurns = 30;
            c.ClaudeCode.AllowedTools = ["Read", "Bash(git:*)"];
        });
        using var _ = dir;
        var engine = new ClaudeCodeEngine(agent, NullAgentEvents.Instance, Options, "/tmp/mcp.json");

        var args = engine.BuildArguments("abc");

        Assert.Equal(["-p", "--output-format", "stream-json", "--verbose", "--append-system-prompt"], args.Take(5));
        Assert.StartsWith("You are Dolly.", args[5]);
        Assert.Contains("send_message", args[5]);
        string[] expectedTail = ["--model", "claude-opus-5-5", "--effort", "high", "--mcp-config", "/tmp/mcp.json", "--strict-mcp-config",
            "--permission-mode", "acceptEdits", "--max-turns", "30", "--resume", "abc", "--allowedTools", "Read", "Bash(git:*)", "mcp__racka"];
        Assert.Equal(expectedTail, args.Skip(6));
    }

    [Fact]
    public async Task RunsClaudeAndResumesSession()
    {
        var (agent, dir) = CreateAgent();
        using var _ = dir;
        var events = new List<AgentEvent>();
        var hub = new AgentEventHub();
        hub.Subscribe(events.Add);
        var engine = new ClaudeCodeEngine(agent, hub, Options, null);

        var first = await engine.RunAsync(AgentTask.Create("count files", TaskSources.Irc, "k0zi", "irc:#racka"), CancellationToken.None);
        var second = await engine.RunAsync(AgentTask.Create("and now?", TaskSources.Irc, "k0zi", "irc:#racka"), CancellationToken.None);

        Assert.True(first.Success, first.Error);
        Assert.Equal("Final answer", first.Output);
        Assert.Equal(150, first.Usage!.InputTokens);
        Assert.Equal(0.0123m, first.Usage.CostUsd);
        Assert.True(second.Success);

        var runs = RecordedArgs(dir);
        Assert.DoesNotContain("--resume", runs[0]);
        Assert.Contains("--resume", runs[1]);
        Assert.Equal("sess-1", runs[1][Array.IndexOf(runs[1], "--resume") + 1]);
        Assert.Contains(events, e => e.Type == AgentEventType.ToolCalled && e.Message == "Read");
        Assert.Contains(events, e => e.Type == AgentEventType.Progress && e.Message == "Working on it");

        var stdin = string.Concat(Directory.GetFiles(Path.Combine(dir.Path, "bin"), "stdin.*").Select(File.ReadAllText));
        Assert.Contains("count files", stdin);
        Assert.Contains("[task", stdin);
        Assert.True(Directory.Exists(agent.WorkDirectory));
    }

    [Fact]
    public async Task StaleSessionIsDroppedAndRetried()
    {
        var (agent, dir) = CreateAgent();
        using var _ = dir;
        new SessionStore(Path.Combine(agent.StateDirectory, "sessions.json")).Set("cli:me", "stale-session");
        var engine = new ClaudeCodeEngine(agent, NullAgentEvents.Instance, Options, null);

        var result = await engine.RunAsync(AgentTask.Create("hi", TaskSources.Cli, "me"), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, RecordedArgs(dir).Count);
        Assert.Equal("sess-1", new SessionStore(Path.Combine(agent.StateDirectory, "sessions.json")).Get("cli:me"));
    }

    [Fact]
    public async Task MissingExecutableFailsCleanly()
    {
        var (agent, dir) = CreateAgent(c => c.ClaudeCode.Executable = "/nonexistent/claude");
        using var _ = dir;
        var engine = new ClaudeCodeEngine(agent, NullAgentEvents.Instance, Options, null);

        var result = await engine.RunAsync(AgentTask.Create("hi", TaskSources.Cli), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void ParsesErrorResult()
    {
        var e = Assert.IsType<ResultEvent>(StreamJsonParser.Parse(
            """{"type":"result","subtype":"error_max_turns","is_error":true,"session_id":"s"}""").Single());
        Assert.True(e.IsError);
        Assert.Equal("error_max_turns", e.Subtype);
        Assert.Empty(StreamJsonParser.Parse("not json"));
        Assert.Empty(StreamJsonParser.Parse("""{"type":"user"}"""));
    }

    [Fact]
    public void WritesMcpConfigWithBridgeAndLinksSkills()
    {
        using var dir = new TempDir();
        dir.Write("skills/shear/SKILL.md", "---\ndescription: d\n---\nbody");
        var config = new AgentConfig { Name = "dolly", Engine = "claude-code" };
        config.McpServers["docs"] = new McpServerConfig { Url = "https://example.com/mcp", Headers = { ["X-Key"] = "k" } };
        config.McpServers["fs"] = new McpServerConfig { Command = "fs-mcp", Args = ["--root", "/"] };
        var agent = new AgentDefinition
        {
            Name = "dolly",
            Directory = Path.Combine(dir.Path, "agent"),
            Config = config,
            Soul = "s",
            Skills = new SkillCatalog([SkillCatalog.LoadSkill(Path.Combine(dir.Path, "skills/shear"))])
        };

        var path = ClaudeCodeWorkspace.WriteMcpConfig(agent, new BridgeCommand("/usr/bin/racka", ["bridge", "--agent", "dolly"]));
        ClaudeCodeWorkspace.LinkSkills(agent);
        ClaudeCodeWorkspace.LinkSkills(agent); // idempotent

        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path!));
        var servers = json.RootElement.GetProperty("mcpServers");
        Assert.Equal("http", servers.GetProperty("docs").GetProperty("type").GetString());
        Assert.Equal("fs-mcp", servers.GetProperty("fs").GetProperty("command").GetString());
        Assert.Equal("bridge", servers.GetProperty("racka").GetProperty("args")[0].GetString());
        var link = new DirectoryInfo(Path.Combine(agent.WorkDirectory, ".claude/skills/shear"));
        Assert.NotNull(link.LinkTarget);
        Assert.True(File.Exists(Path.Combine(link.FullName, "SKILL.md")));
    }
}
