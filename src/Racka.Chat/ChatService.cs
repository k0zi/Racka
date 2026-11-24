using System.Text.Json;
using Microsoft.Extensions.Logging;
using Racka.Core.Engine;
using Racka.Core.Runtime;
using Racka.Core.Tools;

namespace Racka.Chat;

/// <summary>
/// Connects an agent to its chat platforms: operators give it tasks and commands, results are replied
/// to the origin, and lifecycle events are reported to each platform's report channel.
/// </summary>
public sealed class ChatService : IAsyncDisposable
{
    private static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(30);
    private readonly ILogger _logger;
    private readonly IDisposable _eventSubscription;
    private AgentRuntime? _runtime;

    public ChatService(IReadOnlyList<IChatConnector> connectors, AgentEventHub events, ILogger logger)
    {
        Connectors = connectors;
        _logger = logger;
        foreach (var connector in connectors)
            connector.MessageReceived += m => OnMessage(connector, m);
        _eventSubscription = events.Subscribe(OnAgentEvent);
    }

    public IReadOnlyList<IChatConnector> Connectors { get; }

    public IReadOnlyList<string> Platforms => Connectors.Select(c => c.Platform).ToList();

    public IReadOnlyList<string> ConnectedPlatforms => Connectors.Where(c => c.IsConnected).Select(c => c.Platform).ToList();

    public bool CanReport => Connectors.Any(c => c.Config.ReportTarget != null);

    public void Attach(AgentRuntime runtime) => _runtime = runtime;

    /// <summary>Runs every connection; one that fails for good is logged and does not stop the others.</summary>
    public Task RunAsync(CancellationToken ct) => Task.WhenAll(Connectors.Select(c => RunConnectorAsync(c, ct)));

    private async Task RunConnectorAsync(IChatConnector connector, CancellationToken ct)
    {
        try
        {
            await connector.RunAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            _logger.LogError("{Platform} connection stopped: {Message}", connector.Platform, e.Message);
        }
    }

    internal void OnMessage(IChatConnector connector, ChatMessage message)
    {
        if (_runtime == null)
            return;
        var command = ChatCommandParser.Parse(message.Text, connector.Name, connector.Config.CommandPrefix, message.IsPrivate, message.MentionsMe);
        if (command == null)
            return;
        _ = HandleAsync(connector, message, command, connector.IsOperator(message));
    }

    private async Task HandleAsync(IChatConnector connector, ChatMessage message, ChatCommand command, bool isOperator)
    {
        var runtime = _runtime!;
        var config = connector.Config;
        var address = message.IsPrivate ? "" : connector.AddressOf(message);
        var ct = CancellationToken.None;
        try
        {
            switch (command.Kind)
            {
                case ChatCommandKind.Help:
                    var p = config.CommandPrefix;
                    await connector.SendAsync(message.ReplyTo, $"{address}{connector.Name}: <task> | {p}{connector.Name} <task> | " +
                        $"{p}status | {p}stop | {p}reset  (tasks, stop and reset need operator rights)", ct);
                    return;

                case ChatCommandKind.Status:
                    var s = runtime.GetStatus();
                    await connector.SendAsync(message.ReplyTo, $"{address}{s.Name} [{s.Engine}{(s.Model is null ? "" : "/" + s.Model)}] {s.State}" +
                        $"{(s.CurrentTaskId is null ? "" : $" on {s.CurrentTaskId} for {s.CurrentTaskFrom}")}, queue {s.QueueLength}, done {s.CompletedTasks}, failed {s.FailedTasks}", ct);
                    return;
            }

            if (!isOperator)
            {
                await connector.SendNoticeAsync(message, "Sorry, only operators can give me tasks or commands.", ct);
                return;
            }

            var conversationKey = $"{connector.Platform}:{message.ConversationId.ToLowerInvariant()}";
            switch (command.Kind)
            {
                case ChatCommandKind.Stop:
                    await connector.SendAsync(message.ReplyTo, address + (runtime.CancelCurrent() ? "stopping the current task." : "I am idle."), ct);
                    return;

                case ChatCommandKind.Reset:
                    runtime.Engine.ResetConversation(conversationKey);
                    await connector.SendAsync(message.ReplyTo, $"{address}conversation reset.", ct);
                    return;

                case ChatCommandKind.Task:
                    var task = AgentTask.Create(command.Text, connector.Platform, message.SenderName, conversationKey);
                    if (runtime.CurrentTask != null)
                        await connector.SendAsync(message.ReplyTo, $"{address}queued ({runtime.GetStatus().QueueLength + 1} ahead of you).", ct);
                    var result = await runtime.SubmitAsync(task);
                    await ReplyAsync(connector, message.ReplyTo, address, result, ct);
                    return;
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "{Platform} command failed", connector.Platform);
        }
    }

    private static async Task ReplyAsync(IChatConnector connector, ChatAddress target, string address, AgentResult result, CancellationToken ct)
    {
        var text = result.Success ? result.Output : $"failed: {result.Error}" + (string.IsNullOrEmpty(result.Output) ? "" : "\n" + result.Output);
        if (string.IsNullOrWhiteSpace(text))
            text = "(done, no output)";
        var lines = connector.SplitLines(address + text);
        var max = Math.Max(1, connector.Config.MaxReplyLines);
        var reply = string.Join('\n', lines.Take(max));
        if (lines.Count > max)
            reply += $"\n… {lines.Count - max} more lines (full output: racka send or the agent log, task {result.TaskId})";
        await connector.SendAsync(target, reply, ct);
    }

    private void OnAgentEvent(AgentEvent e)
    {
        var text = e.Type switch
        {
            AgentEventType.AgentStarted => $"online ({e.Message})",
            AgentEventType.TaskStarted => $"▶ {e.TaskId}: {e.Message}",
            AgentEventType.TaskCompleted => $"✔ {e.TaskId}: {e.Message}",
            AgentEventType.TaskFailed => $"✖ {e.TaskId}: {e.Message}",
            AgentEventType.Report => e.Message,
            _ => null
        };
        if (text == null)
            return;
        foreach (var connector in Connectors)
        {
            // Lifecycle events only go to an explicit report channel; chat_report falls back to the first channel.
            var target = e.Type == AgentEventType.Report ? connector.Config.ReportTarget : connector.Config.ReportChannel;
            if (target != null)
                _ = SendWhenConnectedAsync(connector, new ChatAddress(target), text);
        }
    }

    private async Task SendWhenConnectedAsync(IChatConnector connector, ChatAddress target, string text)
    {
        try
        {
            var deadline = DateTime.UtcNow + ConnectWait;
            while (!connector.IsConnected && DateTime.UtcNow < deadline)
                await Task.Delay(250);
            await connector.SendAsync(target, text, CancellationToken.None);
        }
        catch (Exception e)
        {
            _logger.LogWarning("Could not post to {Platform} {Target}: {Message}", connector.Platform, target.ChannelId, e.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _eventSubscription.Dispose();
        foreach (var connector in Connectors)
            await connector.DisposeAsync();
    }
}

/// <summary>Lets the model post a one-line status update to the agent's chat report channels.</summary>
public sealed class ChatReportTool(ChatService chat) : RackaTool(
    "chat_report",
    "Post a short one-line status update to the chat report channels, for humans following your work.",
    """{"type":"object","properties":{"text":{"type":"string","description":"One short line."}},"required":["text"]}""")
{
    public override Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        var text = RequireString(arguments, "text").ReplaceLineEndings(" ");
        if (!chat.CanReport)
            return Task.FromResult(ToolResult.Error("No chat report channel is configured."));
        if (chat.ConnectedPlatforms.Count == 0)
            return Task.FromResult(ToolResult.Error("Not connected to chat right now."));
        context.Events.Publish(new AgentEvent(AgentEventType.Report, context.Agent.Name, context.Task.Id, text));
        return Task.FromResult(ToolResult.Ok("Reported."));
    }
}
