using Racka.Core.Engine;
using Racka.Core.Messaging;
using Racka.Core.Runtime;
using Racka.Core.Tools;

namespace Racka.Host;

/// <summary>HTTP/JSON endpoints served on the agent's Unix socket.</summary>
internal static class BusEndpoints
{
    private const int DefaultAskTimeoutSeconds = 600;

    public static void Map(IEndpointRouteBuilder app, AgentRuntime runtime, IReadOnlyList<IRackaTool> bridgedTools)
    {
        app.MapGet("/status", () => runtime.GetStatus());

        app.MapPost("/cancel", () => runtime.CancelCurrent() ? Results.Ok() : Results.NotFound());

        app.MapPost("/messages", (BusMessage message, CancellationToken ct) => HandleMessageAsync(runtime, message, ct));

        app.MapGet("/tools", () => bridgedTools.Select(t => new ToolDescriptor(t.Name, t.Description, t.InputSchema)));

        app.MapPost("/tools/invoke", async (ToolInvocation invocation, CancellationToken ct) =>
        {
            var tool = bridgedTools.FirstOrDefault(t => t.Name == invocation.Name);
            if (tool == null)
                return Results.NotFound();
            // The bridge serves the Claude Code process of the running task.
            var task = runtime.CurrentTask ?? AgentTask.Create("(no task)", TaskSources.Cli, "bridge");
            try
            {
                var result = await tool.InvokeAsync(invocation.Arguments, new ToolContext(runtime.Agent, task, runtime.Events), ct);
                return Results.Ok(new ToolInvocationResult(result.Content, result.IsError));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return Results.Ok(new ToolInvocationResult($"{e.GetType().Name}: {e.Message}", true));
            }
        });
    }

    internal static async Task<IResult> HandleMessageAsync(AgentRuntime runtime, BusMessage message, CancellationToken ct)
    {
        if (message.To != runtime.Agent.Name)
            return Results.BadRequest($"This is agent '{runtime.Agent.Name}', not '{message.To}'.");
        if (string.IsNullOrWhiteSpace(message.Text))
            return Results.BadRequest("Empty message.");

        if (runtime.WouldDeadlock(message))
        {
            var chain = string.Join(" → ", [.. message.Chain ?? [], runtime.Agent.Name]);
            return Results.Ok(new BusReply(message.Id, false, "",
                $"{runtime.Agent.Name} is busy waiting for your own answer (ask cycle: {chain}). Answer without asking {runtime.Agent.Name}."));
        }

        var task = new AgentTask(message.Id, message.Text, message.Source, message.From, message.ConversationKey ?? $"bus:{message.From}")
        {
            Chain = message.Chain ?? []
        };
        var completion = runtime.SubmitAsync(task);
        if (message.Kind == MessageKinds.Tell)
            return Results.Ok(new BusReply(message.Id, true, "", Queued: true));

        var timeout = TimeSpan.FromSeconds(message.TimeoutSeconds ?? DefaultAskTimeoutSeconds);
        try
        {
            var result = await completion.WaitAsync(timeout, ct);
            return Results.Ok(new BusReply(message.Id, result.Success, result.Output, result.Error));
        }
        catch (TimeoutException)
        {
            return Results.Ok(new BusReply(message.Id, false, "", $"timed out after {timeout.TotalSeconds:0}s; the task keeps running"));
        }
    }
}
