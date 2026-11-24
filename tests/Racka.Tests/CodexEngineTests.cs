using Racka.Core.Agents;
using Racka.Core.Config;
using Racka.Core.Engine;
using Racka.Core.Skills;
using Racka.Providers.ClaudeCode;
using Racka.Providers.Codex;

namespace Racka.Tests;

public class CodexEngineTests
{
    private static readonly PromptOptions Options = new(true, true, false);

    // A stand-in for the codex CLI: records its arguments and stdin, then prints `codex exec --json` events.
    private const string FakeCodex = """
        #!/usr/bin/env bash
        dir="$(dirname "$0")"
        printf '%s\n' "$@" > "$dir/args.$$"
        cat > "$dir/stdin.$$"
        if [ "$2" = "resume" ] && [ "$3" = "stale-thread" ]; then
          echo "Error: thread/session not found: stale-thread" >&2
          exit 1
        fi
        if grep -q "fail please" "$dir/stdin.$$"; then
          echo '{"type":"thread.started","thread_id":"thread-9"}'
          echo '{"type":"turn.started"}'
          echo '{"type":"turn.failed","error":{"message":"usage limit reached"}}'
          exit 1
        fi
        echo '{"type":"thread.started","thread_id":"thread-1"}'
        echo '{"type":"turn.started"}'
        echo '{"type":"item.started","item":{"id":"item_0","type":"command_execution","command":"bash -lc ls","status":"in_progress"}}'
        echo '{"type":"item.completed","item":{"id":"item_0","type":"command_execution","command":"bash -lc ls","exit_code":0,"status":"completed"}}'
        echo '{"type":"item.completed","item":{"id":"item_1","type":"mcp_tool_call","server":"racka","tool":"send_message","status":"completed"}}'
        echo '{"type":"item.completed","item":{"id":"item_2","type":"agent_message","text":"Looking around"}}'
        echo '{"type":"item.completed","item":{"id":"item_3","type":"agent_message","text":"There are 3 files."}}'
        echo '{"type":"turn.completed","usage":{"input_tokens":1200,"cached_input_tokens":1000,"output_tokens":40}}'
        """;

    private static (AgentDefinition Agent, TempDir Dir) CreateAgent(Action<AgentConfig>? configure = null)
    {
        var dir = new TempDir();
        var script = dir.Write("bin/codex", FakeCodex);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var config = new AgentConfig { Name = "merino", Engine = "codex" };
        config.Codex.Executable = script;
        configure?.Invoke(config);
        var agent = new AgentDefinition
        {
            Name = "merino",
            Directory = Path.Combine(dir.Path, "agent"),
            Config = config,
            Soul = "You are Merino. \"Quotes\" and ünnepi 🐑 stay intact.",
            Skills = new SkillCatalog([])
        };
        return (agent, dir);
    }

    private static List<string[]> RecordedArgs(TempDir dir) =>
        Directory.GetFiles(Path.Combine(dir.Path, "bin"), "args.*")
            .OrderBy(File.GetLastWriteTimeUtc)
            .Select(File.ReadAllLines)
            .ToList();

    private static string Override(IReadOnlyList<string> args, string key)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "-c" && args[i + 1].StartsWith(key + "=", StringComparison.Ordinal))
                return args[i + 1][(key.Length + 1)..];
        }
        throw new Xunit.Sdk.XunitException($"No -c {key}=… override");
    }

    [Fact]
    public void BuildsArguments()
    {
        var (agent, dir) = CreateAgent(c =>
        {
            c.Model = "gpt-5.5-codex";
            c.Effort = "max";
            c.McpServers["fs"] = new McpServerConfig { Command = "fs-mcp", Args = ["--root", "/"], Env = { ["TOKEN"] = "t" } };
            c.McpServers["docs"] = new McpServerConfig { Url = "https://example.com/mcp", Headers = { ["X-Key"] = "k" } };
            c.Codex.Config["model_verbosity"] = "\"low\"";
            c.Codex.ExtraArgs = ["--ephemeral"];
        });
        using var _ = dir;
        var engine = new CodexEngine(agent, NullAgentEvents.Instance, Options, new BridgeCommand("/usr/bin/racka", ["bridge", "--agent", "merino"]));

        var fresh = engine.BuildArguments(null);
        var resumed = engine.BuildArguments("thread-1");

        Assert.Equal(["exec", "--json", "--skip-git-repo-check", "--model", "gpt-5.5-codex"], fresh.Take(5));
        Assert.Equal(["exec", "resume", "thread-1", "--json"], resumed.Take(4));
        Assert.Equal(["--ephemeral", "-"], fresh.TakeLast(2));
        Assert.Equal("\"workspace-write\"", Override(fresh, "sandbox_mode"));
        Assert.Equal("\"never\"", Override(fresh, "approval_policy"));
        Assert.Equal("\"xhigh\"", Override(fresh, "model_reasoning_effort"));
        Assert.Equal($"{{\"{agent.WorkDirectory}\" = {{trust_level = \"trusted\"}}}}", Override(fresh, "projects"));
        Assert.StartsWith("\"You are Merino. \\\"Quotes\\\" and ünnepi 🐑 stay intact.\\n\\n# Racka runtime", Override(fresh, "developer_instructions"));
        Assert.Equal("{command = \"fs-mcp\", args = [\"--root\", \"/\"], env = {TOKEN = \"t\"}, default_tools_approval_mode = \"approve\"}",
            Override(fresh, "mcp_servers.fs"));
        Assert.Equal("{url = \"https://example.com/mcp\", http_headers = {X-Key = \"k\"}, default_tools_approval_mode = \"approve\"}",
            Override(fresh, "mcp_servers.docs"));
        Assert.Equal("{command = \"/usr/bin/racka\", args = [\"bridge\", \"--agent\", \"merino\"], default_tools_approval_mode = \"approve\"}",
            Override(fresh, "mcp_servers.racka"));
        Assert.Equal("\"low\"", Override(fresh, "model_verbosity"));
    }

    [Fact]
    public void RejectsMcpServerNamesCodexCannotAddress()
    {
        var (agent, dir) = CreateAgent(c => c.McpServers["my.server"] = new McpServerConfig { Command = "x" });
        using var _ = dir;
        Assert.Throws<ConfigException>(() => new CodexEngine(agent, NullAgentEvents.Instance, Options, null));
    }

    [Fact]
    public async Task RunsCodexAndResumesThread()
    {
        var (agent, dir) = CreateAgent();
        using var _ = dir;
        var events = new List<AgentEvent>();
        var hub = new AgentEventHub();
        hub.Subscribe(events.Add);
        var engine = new CodexEngine(agent, hub, Options, null);

        var first = await engine.RunAsync(AgentTask.Create("count files", TaskSources.Bus, "shaun", "bus:shaun"), CancellationToken.None);
        var second = await engine.RunAsync(AgentTask.Create("and now?", TaskSources.Bus, "shaun", "bus:shaun"), CancellationToken.None);

        Assert.True(first.Success, first.Error);
        Assert.Equal("There are 3 files.", first.Output);
        Assert.Equal(1200, first.Usage!.InputTokens);
        Assert.Equal(40, first.Usage.OutputTokens);
        Assert.True(second.Success, second.Error);

        var runs = RecordedArgs(dir);
        Assert.Equal("exec", runs[0][0]);
        Assert.NotEqual("resume", runs[0][1]);
        Assert.Equal(["exec", "resume", "thread-1"], runs[1].Take(3));
        // Started and completed events of one item are reported once per run.
        Assert.Equal(2, events.Count(e => e.Type == AgentEventType.ToolCalled && e.Message == "shell: bash -lc ls"));
        Assert.Contains(events, e => e.Type == AgentEventType.ToolCalled && e.Message == "racka.send_message");
        Assert.Contains(events, e => e.Type == AgentEventType.Progress && e.Message == "Looking around");

        var stdin = string.Concat(Directory.GetFiles(Path.Combine(dir.Path, "bin"), "stdin.*").Select(File.ReadAllText));
        Assert.Contains("[task", stdin);
        Assert.Contains("count files", stdin);
    }

    [Fact]
    public async Task StaleThreadIsDroppedAndRetried()
    {
        var (agent, dir) = CreateAgent();
        using var _ = dir;
        var store = Path.Combine(agent.StateDirectory, "codex-sessions.json");
        new SessionStore(store).Set("cli:me", "stale-thread");
        var engine = new CodexEngine(agent, NullAgentEvents.Instance, Options, null);

        var result = await engine.RunAsync(AgentTask.Create("hi", TaskSources.Cli, "me"), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, RecordedArgs(dir).Count);
        Assert.Equal("thread-1", new SessionStore(store).Get("cli:me"));
    }

    [Fact]
    public async Task FailedTurnReportsTheError()
    {
        var (agent, dir) = CreateAgent();
        using var _ = dir;
        var engine = new CodexEngine(agent, NullAgentEvents.Instance, Options, null);

        var result = await engine.RunAsync(AgentTask.Create("fail please", TaskSources.Cli, "me"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("usage limit reached", result.Error);
        Assert.Null(new SessionStore(Path.Combine(agent.StateDirectory, "codex-sessions.json")).Get("cli:me"));
    }

    [Fact]
    public async Task MissingExecutableFailsCleanly()
    {
        var (agent, dir) = CreateAgent(c => c.Codex.Executable = "/nonexistent/codex");
        using var _ = dir;
        var result = await new CodexEngine(agent, NullAgentEvents.Instance, Options, null)
            .RunAsync(AgentTask.Create("hi", TaskSources.Cli), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void ParserSkipsNoise()
    {
        Assert.Empty(CodexJsonParser.Parse("Reading prompt from stdin..."));
        Assert.Empty(CodexJsonParser.Parse("""{"type":"turn.started"}"""));
        Assert.Empty(CodexJsonParser.Parse("""{"type":"item.started","item":{"id":"i","type":"agent_message","text":"partial"}}"""));
        var error = Assert.IsType<CodexErrorEvent>(CodexJsonParser.Parse("""{"type":"error","message":"reconnecting"}""").Single());
        Assert.False(error.TurnFailed);
        var edit = Assert.IsType<CodexToolEvent>(CodexJsonParser.Parse(
            """{"type":"item.completed","item":{"id":"i","type":"file_change","changes":[{"path":"a.cs","kind":"update"},{"path":"b.cs","kind":"add"}]}}""").Single());
        Assert.Equal("edit: a.cs, b.cs", edit.Description);
    }

    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("a\"b\\c", "\"a\\\"b\\\\c\"")]
    [InlineData("line1\nline2\ttab", "\"line1\\nline2\\ttab\"")]
    [InlineData("bell\u0007", "\"bell\\u0007\"")]
    [InlineData("árvíztűrő 🐑", "\"árvíztűrő 🐑\"")]
    public void TomlStringsAreEscaped(string value, string expected) => Assert.Equal(expected, Toml.String(value));
}
