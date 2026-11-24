using System.Diagnostics;
using System.Globalization;
using System.Text;
using HerdFlow;
using Racka.Core.Agents;
using Racka.Core.Engine;

namespace Racka.Providers.ClaudeCode;

/// <summary>HerdFlow shared state for one Claude Code run.</summary>
public sealed class ClaudeCodeContext
{
    public required AgentDefinition Agent { get; init; }
    public required AgentTask Task { get; init; }
    public required IAgentEvents Events { get; init; }
    public required CancellationToken CancellationToken { get; init; }

    public string? SessionId { get; set; }
    public ResultEvent? Result { get; set; }
    public string? NewSessionId { get; set; }
    public string Stderr { get; set; } = "";
    public int ExitCode { get; set; }
    public bool ResumeRetried { get; set; }
}

/// <summary>
/// Runs tasks through the Claude Code CLI (`claude -p --output-format stream-json`).
/// Flow: Prepare → Exec ─ok→ Finalize, Exec ─resume_failed→ DropSession → Exec, Exec ─failed→ Finalize.
/// Claude Code runs its own tool loop, MCP servers and skills.
/// </summary>
public sealed class ClaudeCodeEngine : IAgentEngine
{
    internal const string ActionOk = "ok";
    internal const string ActionFailed = "failed";
    internal const string ActionResumeFailed = "resume_failed";

    private readonly AgentDefinition _agent;
    private readonly IAgentEvents _events;
    private readonly SessionStore _sessions;
    private readonly PromptOptions _promptOptions;
    private readonly AsyncFlow _flow;

    public ClaudeCodeEngine(AgentDefinition agent, IAgentEvents events, PromptOptions promptOptions, string? mcpConfigPath)
    {
        _agent = agent;
        _events = events;
        _sessions = new SessionStore(Path.Combine(agent.StateDirectory, "sessions.json"));
        _promptOptions = promptOptions;
        McpConfigPath = mcpConfigPath;

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
    public string SystemPrompt => PromptBuilder.BuildSystemPrompt(_agent, _promptOptions);
    public string? McpConfigPath { get; }

    public async Task<AgentResult> RunAsync(AgentTask task, CancellationToken ct)
    {
        var ctx = new ClaudeCodeContext { Agent = _agent, Task = task, Events = _events, CancellationToken = ct };
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

        if (ctx.Result is not { } result)
        {
            var stderr = ctx.Stderr.Trim();
            return AgentResult.Failed(task.Id, $"claude exited with code {ctx.ExitCode} without a result{(stderr.Length > 0 ? ": " + Tail(stderr, 500) : "")}");
        }
        var usage = new AgentUsage(result.InputTokens, result.OutputTokens, result.CostUsd);
        return result.IsError
            ? new AgentResult(task.Id, false, result.Result, $"claude reported {result.Subtype}", usage)
            : new AgentResult(task.Id, true, result.Result.Trim(), null, usage);
    }

    public void ResetConversation(string conversationKey) => _sessions.Remove(conversationKey);

    public IReadOnlyList<string> BuildArguments(string? sessionId)
    {
        var cc = _agent.Config.ClaudeCode;
        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose", "--append-system-prompt", SystemPrompt };
        if (_agent.Config.Model is { } model)
            args.AddRange(["--model", model]);
        if (_agent.Config.Effort is { } effort)
            args.AddRange(["--effort", effort]);
        if (McpConfigPath != null)
            args.AddRange(["--mcp-config", McpConfigPath]);
        if (cc.StrictMcpConfig)
            args.Add("--strict-mcp-config");
        if (cc.PermissionMode is { } mode)
            args.AddRange(["--permission-mode", mode]);
        if (cc.MaxTurns is { } turns)
            args.AddRange(["--max-turns", turns.ToString(CultureInfo.InvariantCulture)]);
        if (cc.MaxBudgetUsd is { } budget)
            args.AddRange(["--max-budget-usd", budget.ToString(CultureInfo.InvariantCulture)]);
        if (sessionId != null)
            args.AddRange(["--resume", sessionId]);
        args.AddRange(cc.ExtraArgs);
        // Variadic options last: they consume values until the next flag.
        // Print mode cannot ask for permission, so the Racka bridge and the agent's own MCP servers are pre-approved.
        List<string> allowed = [.. cc.AllowedTools];
        if (McpConfigPath != null)
        {
            foreach (var server in _agent.Config.McpServers.Keys.Order(StringComparer.Ordinal).Append(ClaudeCodeWorkspace.BridgeServerName))
            {
                if (!allowed.Contains("mcp__" + server))
                    allowed.Add("mcp__" + server);
            }
        }
        if (allowed.Count > 0)
            args.AddRange(["--allowedTools", .. allowed]);
        if (cc.DisallowedTools.Count > 0)
            args.AddRange(["--disallowedTools", .. cc.DisallowedTools]);
        return args;
    }

    internal async Task<(ResultEvent? Result, string? SessionId, string Stderr, int ExitCode)> RunProcessAsync(
        ClaudeCodeContext ctx, CancellationToken ct)
    {
        Directory.CreateDirectory(_agent.WorkDirectory);
        var psi = new ProcessStartInfo(_agent.Config.ClaudeCode.Executable)
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

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start claude.");
        await using var kill = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });

        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.StandardInput.WriteAsync(PromptBuilder.BuildUserMessage(ctx.Task));
        process.StandardInput.Close();

        ResultEvent? result = null;
        string? sessionId = null;
        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
        {
            foreach (var e in StreamJsonParser.Parse(line))
            {
                switch (e)
                {
                    case InitEvent init:
                        sessionId = init.SessionId;
                        break;
                    case ToolUseEvent tool:
                        _events.Publish(new AgentEvent(AgentEventType.ToolCalled, _agent.Name, ctx.Task.Id, tool.Name));
                        break;
                    case AssistantTextEvent text:
                        _events.Publish(new AgentEvent(AgentEventType.Progress, _agent.Name, ctx.Task.Id, text.Text.Trim()));
                        break;
                    case ResultEvent r:
                        result = r;
                        sessionId = r.SessionId ?? sessionId;
                        break;
                }
            }
        }
        await process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        return (result, sessionId, await stderrTask, process.ExitCode);
    }

    private static string Tail(string s, int max) => s.Length <= max ? s : "…" + s[^max..];

    private sealed class PrepareNode(SessionStore sessions) : AsyncNode
    {
        protected override Task<object?> PrepAsync(object shared)
        {
            var ctx = (ClaudeCodeContext)shared;
            ctx.SessionId = sessions.Get(ctx.Task.ConversationKey);
            return Task.FromResult<object?>(null);
        }
    }

    private sealed class ExecNode(ClaudeCodeEngine engine) : AsyncNode
    {
        protected override Task<object?> PrepAsync(object shared) => Task.FromResult<object?>(shared);

        protected override async Task<object?> ExecAsync(object? prepRes)
        {
            var ctx = (ClaudeCodeContext)prepRes!;
            var (result, sessionId, stderr, exitCode) = await engine.RunProcessAsync(ctx, ctx.CancellationToken);
            ctx.Result = result;
            ctx.NewSessionId = sessionId;
            ctx.Stderr = stderr;
            ctx.ExitCode = exitCode;
            return null;
        }

        protected override Task<object?> PostAsync(object shared, object? prepRes, object? execRes)
        {
            var ctx = (ClaudeCodeContext)shared;
            string action;
            if (ctx.Result is { IsError: false })
                action = ActionOk;
            else if (ctx.SessionId != null && !ctx.ResumeRetried && LooksLikeMissingSession(ctx))
                action = ActionResumeFailed;
            else
                action = ActionFailed;
            return Task.FromResult<object?>(action);
        }

        private static bool LooksLikeMissingSession(ClaudeCodeContext ctx)
        {
            var text = (ctx.Stderr + " " + ctx.Result?.Result).ToLowerInvariant();
            return text.Contains("no conversation found") || text.Contains("session") && text.Contains("not found");
        }
    }

    private sealed class DropSessionNode(SessionStore sessions) : AsyncNode
    {
        protected override Task<object?> PrepAsync(object shared)
        {
            var ctx = (ClaudeCodeContext)shared;
            sessions.Remove(ctx.Task.ConversationKey);
            ctx.SessionId = null;
            ctx.ResumeRetried = true;
            ctx.Result = null;
            return Task.FromResult<object?>(null);
        }
    }

    private sealed class FinalizeNode(SessionStore sessions) : AsyncNode
    {
        protected override Task<object?> PrepAsync(object shared)
        {
            var ctx = (ClaudeCodeContext)shared;
            if (ctx.NewSessionId != null && ctx.Result != null)
                sessions.Set(ctx.Task.ConversationKey, ctx.NewSessionId);
            return Task.FromResult<object?>(null);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
