using HerdFlow;
using Racka.Core.Llm;
using Racka.Core.Tools;

namespace Racka.Core.Engine.ApiLoop;

public static class ApiLoopActions
{
    public const string ToolCalls = "tool_calls";
    public const string Done = "done";
    public const string Limit = "limit";
}

/// <summary>Appends the task as a user message to the conversation.</summary>
public sealed class BuildPromptNode : AsyncNode
{
    protected override Task<object?> PrepAsync(object shared)
    {
        var ctx = (ApiLoopContext)shared;
        ctx.Conversation.AddUserText(PromptBuilder.BuildUserMessage(ctx.Task));
        return Task.FromResult<object?>(null);
    }
}

/// <summary>
/// Asks the model for the next turn. The provider SDKs already retry transient HTTP failures,
/// so the node does not retry by default; the provider leaves the conversation untouched on error.
/// </summary>
public sealed class LlmTurnNode : AsyncNode
{
    private readonly ILlmProvider _provider;

    public LlmTurnNode(ILlmProvider provider, int maxRetries = 1, int wait = 0) : base(maxRetries, wait)
        => _provider = provider;

    protected override Task<object?> PrepAsync(object shared) => Task.FromResult<object?>(shared);

    protected override async Task<object?> ExecAsync(object? prepRes)
    {
        var ctx = (ApiLoopContext)prepRes!;
        ctx.CancellationToken.ThrowIfCancellationRequested();
        return await _provider.NextTurnAsync(ctx.Conversation, ctx.Tools, ctx.CancellationToken);
    }

    protected override Task<object?> ExecFallbackAsync(object? prepRes, Exception exc) =>
        Task.FromException<object?>(exc);

    protected override Task<object?> PostAsync(object shared, object? prepRes, object? execRes)
    {
        var ctx = (ApiLoopContext)shared;
        var turn = (LlmTurn)execRes!;
        ctx.LastTurn = turn;
        ctx.Iterations++;
        if (turn.Usage is { } usage)
        {
            ctx.InputTokens += usage.InputTokens;
            ctx.OutputTokens += usage.OutputTokens;
        }
        if (!string.IsNullOrWhiteSpace(turn.Text) && turn.HasToolCalls)
            ctx.Events.Publish(new AgentEvent(AgentEventType.Progress, ctx.Agent.Name, ctx.Task.Id, turn.Text.Trim()));

        string action = !turn.HasToolCalls ? ApiLoopActions.Done
            : ctx.Iterations >= ctx.Agent.Config.MaxIterations ? ApiLoopActions.Limit
            : ApiLoopActions.ToolCalls;
        return Task.FromResult<object?>(action);
    }
}

/// <summary>Runs every tool call of the last turn in parallel and appends the results in one message.</summary>
public sealed class ToolExecNode : AsyncNode
{
    protected override Task<object?> PrepAsync(object shared) => Task.FromResult<object?>(shared);

    protected override async Task<object?> ExecAsync(object? prepRes)
    {
        var ctx = (ApiLoopContext)prepRes!;
        var calls = ctx.LastTurn!.ToolCalls;
        var results = await Task.WhenAll(calls.Select(call => InvokeAsync(ctx, call)));
        ctx.Conversation.AddToolResults(results);
        return null;
    }

    private static async Task<ToolCallResult> InvokeAsync(ApiLoopContext ctx, ToolCall call)
    {
        var tool = ctx.Tools.FirstOrDefault(t => t.Name == call.Name);
        if (tool == null)
            return new ToolCallResult(call, ToolResult.Error($"Unknown tool '{call.Name}'."));

        ctx.Events.Publish(new AgentEvent(AgentEventType.ToolCalled, ctx.Agent.Name, ctx.Task.Id, call.Name));
        try
        {
            var result = await tool.InvokeAsync(call.Arguments, ctx.ToolContext, ctx.CancellationToken);
            return new ToolCallResult(call, result);
        }
        catch (OperationCanceledException) when (ctx.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return new ToolCallResult(call, ToolResult.Error($"{e.GetType().Name}: {e.Message}"));
        }
    }
}

/// <summary>Stores the final answer of the model.</summary>
public sealed class FinalizeNode : AsyncNode
{
    protected override Task<object?> PrepAsync(object shared)
    {
        var ctx = (ApiLoopContext)shared;
        ctx.Output = ctx.LastTurn?.Text.Trim() ?? "";
        return Task.FromResult<object?>(null);
    }
}

/// <summary>
/// Ends a run that exceeded maxIterations. The pending tool calls get error results so the
/// conversation stays valid for the next task.
/// </summary>
public sealed class IterationLimitNode : AsyncNode
{
    protected override Task<object?> PrepAsync(object shared)
    {
        var ctx = (ApiLoopContext)shared;
        var message = $"Not executed: iteration limit ({ctx.Agent.Config.MaxIterations}) reached.";
        ctx.Conversation.AddToolResults(ctx.LastTurn!.ToolCalls.Select(c => new ToolCallResult(c, ToolResult.Error(message))).ToList());
        ctx.HitIterationLimit = true;
        var partial = ctx.LastTurn.Text.Trim();
        ctx.Output = string.IsNullOrEmpty(partial)
            ? $"Stopped: iteration limit ({ctx.Agent.Config.MaxIterations}) reached before finishing."
            : $"{partial}\n\n(Stopped: iteration limit of {ctx.Agent.Config.MaxIterations} reached before finishing.)";
        return Task.FromResult<object?>(null);
    }
}
