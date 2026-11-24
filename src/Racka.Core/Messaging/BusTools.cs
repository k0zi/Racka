using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Racka.Core.Tools;

namespace Racka.Core.Messaging;

/// <summary>Lets the model message another agent over the bus.</summary>
public sealed class SendMessageTool(BusClient bus, string self) : RackaTool(
    "send_message",
    "Send a message to another Racka agent. With wait_for_reply=true (default) you get the agent's final answer; " +
    "with false the message is queued and you do not get an answer.",
    """
    {"type":"object","properties":{
      "to":{"type":"string","description":"Name of the target agent (see list_agents)."},
      "text":{"type":"string","description":"The task or question for the agent. Make it self-contained."},
      "wait_for_reply":{"type":"boolean","description":"Wait for the answer (default true)."},
      "timeout_s":{"type":"integer","description":"Max seconds to wait for the answer (default 600)."}
    },"required":["to","text"]}
    """)
{
    public override async Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        var to = RequireString(arguments, "to");
        var text = RequireString(arguments, "text");
        var wait = OptionalBool(arguments, "wait_for_reply", true);
        var timeout = Math.Clamp(OptionalInt(arguments, "timeout_s", 600), 5, 3600);

        if (to == self)
            return ToolResult.Error("You cannot send a message to yourself.");
        if (!bus.Exists(to))
            return ToolResult.Error($"Agent '{to}' is not running. Use list_agents to see who is available.");

        var message = new BusMessage(
            Guid.NewGuid().ToString("n")[..12],
            self,
            to,
            wait ? MessageKinds.Ask : MessageKinds.Tell,
            text,
            ConversationKey: $"bus:{self}",
            TimeoutSeconds: timeout,
            Chain: wait ? [.. context.Task.Chain, self] : []);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout + 5));
        try
        {
            var reply = await bus.SendAsync(message, timeoutCts.Token);
            if (!wait)
                return ToolResult.Ok($"Message queued for {to}.");
            return reply.Success
                ? ToolResult.Ok(reply.Output)
                : ToolResult.Error($"{to} failed: {reply.Error}{(string.IsNullOrEmpty(reply.Output) ? "" : "\n" + reply.Output)}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolResult.Error($"No reply from {to} within {timeout} seconds.");
        }
        catch (Exception e) when (e is HttpRequestException or SocketException)
        {
            return ToolResult.Error($"Could not reach {to}: {e.Message}");
        }
    }
}

public sealed class ListAgentsTool(BusClient bus, string self) : RackaTool(
    "list_agents",
    "List the running Racka agents with their engine and current state.",
    """{"type":"object","properties":{}}""")
{
    public override async Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        var agents = await bus.ListAgentsAsync(ct);
        var sb = new StringBuilder();
        foreach (var a in agents.Where(a => a.Name != self))
            sb.AppendLine($"- {a.Name}: {a.Engine}{(a.Model is null ? "" : "/" + a.Model)}, {a.State}{(a.QueueLength > 0 ? $", {a.QueueLength} queued" : "")}");
        return ToolResult.Ok(sb.Length == 0 ? "No other agents are running." : sb.ToString().TrimEnd());
    }
}
