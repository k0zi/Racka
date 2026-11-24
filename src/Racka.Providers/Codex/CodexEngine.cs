using System.Diagnostics;
using System.Text;
using HerdFlow;
using Racka.Core.Agents;
using Racka.Core.Config;
using Racka.Core.Engine;
using Racka.Providers.ClaudeCode;

namespace Racka.Providers.Codex;

/// <summary>HerdFlow shared state for one Codex run.</summary>
public sealed class CodexContext
{
    public required AgentDefinition Agent { get; init; }
    public required AgentTask Task { get; init; }
    public required CancellationToken CancellationToken { get; init; }

    public string? SessionId { get; set; }
    public CodexRun? Run { get; set; }
    public bool ResumeRetried { get; set; }
}

/// <summary>What one `codex exec` process produced.</summary>
public sealed record CodexRun(string? ThreadId, string? LastMessage, bool TurnCompleted, string? Error,
    long InputTokens, long OutputTokens, string Stderr, int ExitCode)
{
    public bool Succeeded => TurnCompleted && Error == null && ExitCode == 0;
}

/// <summary>
/// Runs tasks through the OpenAI Codex CLI (`codex exec --json`).
/// Flow: Prepare → Exec ─ok→ Finalize, Exec ─resume_failed→ DropSession → Exec, Exec ─failed→ Finalize.
/// Codex runs its own tool loop; the soul and runtime prompt go in as developer instructions, and the Racka tools
/// (messaging, memory, skills, IRC) come from the bridge MCP server.
/// </summary>
public sealed class CodexEngine : IAgentEngine
{
    internal const string ActionOk = "ok";
    internal const string ActionFailed = "failed";
    internal const string ActionResumeFailed = "resume_failed";

    private readonly AgentDefinition _agent;
    private readonly IAgentEvents _events;
    private readonly PromptOptions _promptOptions;
    private readonly BridgeCommand? _bridge;
    private readonly SessionStore _sessions;
    private readonly AsyncFlow _flow;

    public CodexEngine(AgentDefinition agent, IAgentEvents events, PromptOptions promptOptions, BridgeCommand? bridge)
    {
        foreach (var name in agent.Config.McpServers.Keys)
        {
            if (Toml.Key(name) != name)
                throw new ConfigException($"MCP server name '{name}' cannot be used with Codex: use letters, digits, '-' and '_'.");
        }
        _agent = agent;
        _events = events;
        _promptOptions = promptOptions;
        _bridge = bridge;
        _sessions = new SessionStore(Path.Combine(agent.StateDirectory, "codex-sessions.json"));

        var prepare = new PrepareNode(_sessions);
        var exec = new ExecNode(this);
        var drop = new DropSessionNode(_sessions);
        var finalize = new FinalizeNode(_sessions);
        prepare.Then(exec);
        exec.On(ActionOk).Then(finalize);
        exec.On(ActionFailed).Then(finalize);
        exec.On(ActionResumeFailed).Then(drop);
        drop.Then(exec);
        _flow = new AsyncFlow(prepare);
    }

    /// <summary>Rebuilt for every run, so it includes the current memory index.</summary>
    public string Instructions => PromptBuilder.BuildSystemPrompt(_agent, _promptOptions);

    public async Task<AgentResult> RunAsync(AgentTask task, CancellationToken ct)
    {
        var ctx = new CodexContext { Agent = _agent, Task = task, CancellationToken = ct };
        try
        {
            await _flow.RunAsync(ctx);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return AgentResult.Failed(task.Id, "cancelled");
        }
        catch (Exception e)
        {
            return AgentResult.Failed(task.Id, $"{e.GetType().Name}: {e.Message}");
        }

        var run = ctx.Run!;
        var usage = new AgentUsage(run.InputTokens, run.OutputTokens);
        if (run.Succeeded)
            return new AgentResult(task.Id, true, (run.LastMessage ?? "").Trim(), null, usage);
        var stderr = run.Stderr.Trim();
        var error = run.Error
            ?? (stderr.Length > 0 ? Tail(stderr, 500) : $"codex exited with code {run.ExitCode} without finishing the turn");
        return new AgentResult(task.Id, false, run.LastMessage ?? "", error, usage);
    }

    public void ResetConversation(string conversationKey) => _sessions.Remove(conversationKey);

    public IReadOnlyList<string> BuildArguments(string? sessionId)
    {
        var codex = _agent.Config.Codex;
        var args = new List<string> { "exec" };
        // Options go after `resume <id>`: the resume subcommand accepts the same global options.
        if (sessionId != null)
            args.AddRange(["resume", sessionId]);
        args.Add("--json");
        if (codex.SkipGitRepoCheck)
            args.Add("--skip-git-repo-check");
        if (codex.IgnoreUserConfig)
            args.Add("--ignore-user-config");
        if (_agent.Config.Model is { } model)
            args.AddRange(["--model", model]);

        Override(args, "sandbox_mode", Toml.String(codex.Sandbox));
        Override(args, "approval_policy", Toml.String(codex.ApprovalPolicy));
        if (codex.TrustWorkdir)
            Override(args, "projects", $"{{{Toml.String(_agent.WorkDirectory)} = {{trust_level = \"trusted\"}}}}");
        if (_agent.Config.Effort is { } effort)
            Override(args, "model_reasoning_effort", Toml.String(effort == "max" ? "xhigh" : effort));
        Override(args, "developer_instructions", Toml.String(Instructions));

        foreach (var (name, server) in _agent.Config.McpServers.OrderBy(s => s.Key, StringComparer.Ordinal))
            Override(args, "mcp_servers." + name, McpServerTable(server));
        if (_bridge != null)
        {
            Override(args, "mcp_servers." + ClaudeCodeWorkspace.BridgeServerName,
                $"{{command = {Toml.String(_bridge.Command)}, args = {Toml.Array(_bridge.Args)}, default_tools_approval_mode = \"approve\"}}");
        }
        foreach (var (key, value) in codex.Config)
            Override(args, key, value);

        args.AddRange(codex.ExtraArgs);
        args.Add("-"); // the task arrives on stdin
        return args;
    }

    private static void Override(List<string> args, string key, string tomlValue) => args.AddRange(["-c", $"{key}={tomlValue}"]);

    private static string McpServerTable(McpServerConfig server)
    {
        // The agent's own servers were configured on purpose, so their tools run without approval prompts.
        var parts = new List<string>();
        if (server.IsStdio)
        {
            parts.Add($"command = {Toml.String(server.Command!)}");
            parts.Add($"args = {Toml.Array(server.Args)}");
            if (server.Env.Count > 0)
                parts.Add($"env = {Toml.Table(server.Env)}");
        }
        else
        {
            parts.Add($"url = {Toml.String(server.Url!)}");
            if (server.Headers.Count > 0)
                parts.Add($"http_headers = {Toml.Table(server.Headers)}");
        }
        parts.Add("default_tools_approval_mode = \"approve\"");
        return "{" + string.Join(", ", parts) + "}";
    }

    internal async Task<CodexRun> RunProcessAsync(CodexContext ctx, CancellationToken ct)
    {
        Directory.CreateDirectory(_agent.WorkDirectory);
        var psi = new ProcessStartInfo(_agent.Config.Codex.Executable)
        {
            WorkingDirectory = _agent.WorkDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in BuildArguments(ctx.SessionId))
            psi.ArgumentList.Add(arg);
        psi.Environment["RACKA_AGENT"] = _agent.Name;
        psi.Environment["RACKA_TASK_ID"] = ctx.Task.Id;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start codex.");
        await using var kill = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });

        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.StandardInput.WriteAsync(PromptBuilder.BuildUserMessage(ctx.Task));
        process.StandardInput.Close();

        string? threadId = null, lastMessage = null, error = null;
        var turnCompleted = false;
        long input = 0, output = 0;
        var reported = new HashSet<string>();
        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
        {
            foreach (var e in CodexJsonParser.Parse(line))
            {
                switch (e)
                {
                    case ThreadStartedEvent started:
                        threadId = started.ThreadId;
                        break;
                    case CodexToolEvent tool when reported.Add(tool.ItemId):
                        _events.Publish(new AgentEvent(AgentEventType.ToolCalled, _agent.Name, ctx.Task.Id, tool.Description));
                        break;
                    case AgentMessageEvent message:
                        lastMessage = message.Text;
                        _events.Publish(new AgentEvent(AgentEventType.Progress, _agent.Name, ctx.Task.Id, message.Text.Trim()));
                        break;
                    case TurnCompletedEvent turn:
                        turnCompleted = true;
                        input += turn.InputTokens;
                        output += turn.OutputTokens;
                        break;
                    case CodexErrorEvent failure when failure.TurnFailed || error == null:
                        error = failure.Message;
                        break;
                }
            }
        }
        await process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        // A turn that completed after a transient error (e.g. a retried stream) is still a success.
        if (turnCompleted && process.ExitCode == 0)
            error = null;
        return new CodexRun(threadId, lastMessage, turnCompleted, error, input, output, await stderrTask, process.ExitCode);
    }

    private static string Tail(string s, int max) => s.Length <= max ? s : "…" + s[^max..];

    private sealed class PrepareNode(SessionStore sessions) : AsyncNode
    {
        protected override Task<object?> PrepAsync(object shared)
        {
            var ctx = (CodexContext)shared;
            ctx.SessionId = sessions.Get(ctx.Task.ConversationKey);
            return Task.FromResult<object?>(null);
        }
    }

    private sealed class ExecNode(CodexEngine engine) : AsyncNode
    {
        protected override Task<object?> PrepAsync(object shared) => Task.FromResult<object?>(shared);

        protected override async Task<object?> ExecAsync(object? prepRes)
        {
            var ctx = (CodexContext)prepRes!;
            ctx.Run = await engine.RunProcessAsync(ctx, ctx.CancellationToken);
            return null;
        }

        protected override Task<object?> PostAsync(object shared, object? prepRes, object? execRes)
        {
            var ctx = (CodexContext)shared;
            string action;
            if (ctx.Run!.Succeeded)
                action = ActionOk;
            // A resume that never started a thread could not load the session: start over once without it.
            else if (ctx.SessionId != null && !ctx.ResumeRetried && ctx.Run.ThreadId == null)
                action = ActionResumeFailed;
            else
                action = ActionFailed;
            return Task.FromResult<object?>(action);
        }
    }

    private sealed class DropSessionNode(SessionStore sessions) : AsyncNode
    {
        protected override Task<object?> PrepAsync(object shared)
        {
            var ctx = (CodexContext)shared;
            sessions.Remove(ctx.Task.ConversationKey);
            ctx.SessionId = null;
            ctx.ResumeRetried = true;
            ctx.Run = null;
            return Task.FromResult<object?>(null);
        }
    }

    private sealed class FinalizeNode(SessionStore sessions) : AsyncNode
    {
        protected override Task<object?> PrepAsync(object shared)
        {
            var ctx = (CodexContext)shared;
            if (ctx.Run is { ThreadId: { } threadId, TurnCompleted: true })
                sessions.Set(ctx.Task.ConversationKey, threadId);
            return Task.FromResult<object?>(null);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
