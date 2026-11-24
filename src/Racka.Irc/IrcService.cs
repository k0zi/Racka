using System.Text.Json;
using Microsoft.Extensions.Logging;
using Racka.Core.Config;
using Racka.Core.Engine;
using Racka.Core.Runtime;
using Racka.Core.Tools;

namespace Racka.Irc;

/// <summary>
/// Connects an agent to IRC: operators give it tasks and commands, results are replied
/// to the origin, and lifecycle events are reported to the report channel.
/// </summary>
public sealed class IrcService : IAsyncDisposable
{
    private readonly IrcConfig _config;
    private readonly string _agentName;
    private readonly IrcConnection _connection;
    private readonly ILogger _logger;
    private readonly IDisposable _eventSubscription;
    private AgentRuntime? _runtime;

    public IrcService(IrcConfig config, string agentName, AgentEventHub events, ILoggerFactory loggerFactory)
    {
        _config = config;
        _agentName = agentName;
        _logger = loggerFactory.CreateLogger($"racka.{agentName}.irc");
        var password = config.SaslPasswordEnv is { } env ? Environment.GetEnvironmentVariable(env) : null;
        if (config.SaslPasswordEnv != null && string.IsNullOrEmpty(password))
            throw new ConfigException($"Environment variable '{config.SaslPasswordEnv}' (irc.saslPasswordEnv) is not set.");
        var channels = config.Channels.ToList();
        if (config.ReportChannel is { } report && !channels.Contains(report, StringComparer.OrdinalIgnoreCase))
            channels.Add(report);

        _connection = new IrcConnection(new IrcConnectionOptions(
            config.Server, config.Port, config.Tls, config.Nick,
            config.User ?? config.Nick, config.RealName ?? $"Racka agent {agentName}",
            channels, password), _logger);
        _connection.MessageReceived += OnMessage;
        _eventSubscription = events.Subscribe(OnAgentEvent);
    }

    public bool IsConnected => _connection.IsConnected;

    /// <summary>The report channel, or the first configured channel.</summary>
    public string? ReportTarget => _config.ReportChannel ?? _config.Channels.FirstOrDefault();

    public void Attach(AgentRuntime runtime) => _runtime = runtime;

    public Task RunAsync(CancellationToken ct) => _connection.RunAsync(ct);

    public void Report(string text)
    {
        if (ReportTarget is { } target)
            _connection.SendMessage(target, text);
    }

    internal void OnMessage(IrcMessage message)
    {
        if (message.Command != "PRIVMSG" || message.Parameters.Count < 2 || message.Prefix is null)
            return;
        var target = message.Parameters[0];
        var text = message.Parameters[1];
        if (text.StartsWith('\u0001'))
            return; // CTCP
        var isPrivate = string.Equals(target, _connection.CurrentNick, StringComparison.OrdinalIgnoreCase);
        var replyTo = isPrivate ? message.Nick! : target;
        var command = IrcCommandParser.Parse(text, _connection.CurrentNick, _config.CommandPrefix, isPrivate);
        if (command == null || _runtime == null)
            return;

        var isOperator = _config.Operators.Any(mask => IrcText.MatchesMask(message.Prefix, mask));
        _ = HandleAsync(command, message.Nick!, replyTo, isPrivate, isOperator);
    }

    private async Task HandleAsync(IrcCommand command, string nick, string replyTo, bool isPrivate, bool isOperator)
    {
        var runtime = _runtime!;
        var address = isPrivate ? "" : $"{nick}: ";
        try
        {
            switch (command.Kind)
            {
                case IrcCommandKind.Help:
                    _connection.SendMessage(replyTo, $"{address}{_connection.CurrentNick}: <task> | {_config.CommandPrefix}{_connection.CurrentNick} <task> | " +
                        $"{_config.CommandPrefix}status | {_config.CommandPrefix}stop | {_config.CommandPrefix}reset  (tasks, stop and reset need operator rights)");
                    return;

                case IrcCommandKind.Status:
                    var s = runtime.GetStatus();
                    _connection.SendMessage(replyTo, $"{address}{s.Name} [{s.Engine}{(s.Model is null ? "" : "/" + s.Model)}] {s.State}" +
                        $"{(s.CurrentTaskId is null ? "" : $" on {s.CurrentTaskId} for {s.CurrentTaskFrom}")}, queue {s.QueueLength}, done {s.CompletedTasks}, failed {s.FailedTasks}");
                    return;
            }

            if (!isOperator)
            {
                _connection.SendMessage(nick, "Sorry, only operators can give me tasks or commands.", notice: true);
                return;
            }

            switch (command.Kind)
            {
                case IrcCommandKind.Stop:
                    _connection.SendMessage(replyTo, address + (runtime.CancelCurrent() ? "stopping the current task." : "I am idle."));
                    return;

                case IrcCommandKind.Reset:
                    runtime.Engine.ResetConversation(ConversationKey(isPrivate, nick, replyTo));
                    _connection.SendMessage(replyTo, $"{address}conversation reset.");
                    return;

                case IrcCommandKind.Task:
                    var task = AgentTask.Create(command.Text, TaskSources.Irc, nick, ConversationKey(isPrivate, nick, replyTo));
                    if (runtime.CurrentTask != null)
                        _connection.SendMessage(replyTo, $"{address}queued ({runtime.GetStatus().QueueLength + 1} ahead of you).");
                    var result = await runtime.SubmitAsync(task);
                    Reply(replyTo, address, result);
                    return;
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "IRC command failed");
        }
    }

    private static string ConversationKey(bool isPrivate, string nick, string channel) =>
        isPrivate ? $"irc:{nick.ToLowerInvariant()}" : $"irc:{channel.ToLowerInvariant()}";

    private void Reply(string target, string address, AgentResult result)
    {
        var text = result.Success ? result.Output : $"failed: {result.Error}" + (string.IsNullOrEmpty(result.Output) ? "" : "\n" + result.Output);
        if (string.IsNullOrWhiteSpace(text))
            text = "(done, no output)";
        var lines = IrcText.SplitLines(address + text);
        var max = Math.Max(1, _config.MaxReplyLines);
        foreach (var line in lines.Take(max))
            _connection.SendMessage(target, line);
        if (lines.Count > max)
            _connection.SendMessage(target, $"… {lines.Count - max} more lines (full output: racka send or the agent log, task {result.TaskId})");
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
        if (text != null && _config.ReportChannel != null)
            _connection.SendMessage(_config.ReportChannel, text);
    }

    public ValueTask DisposeAsync()
    {
        _eventSubscription.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Lets the model post a one-line status update to the agent's IRC report channel.</summary>
public sealed class IrcReportTool(IrcService irc) : RackaTool(
    "irc_report",
    "Post a short one-line status update to the IRC report channel, for humans following your work.",
    """{"type":"object","properties":{"text":{"type":"string","description":"One short line."}},"required":["text"]}""")
{
    public override Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        var text = RequireString(arguments, "text").ReplaceLineEndings(" ");
        if (irc.ReportTarget == null)
            return Task.FromResult(ToolResult.Error("No IRC report channel is configured."));
        if (!irc.IsConnected)
            return Task.FromResult(ToolResult.Error("Not connected to IRC right now."));
        context.Events.Publish(new AgentEvent(AgentEventType.Report, context.Agent.Name, context.Task.Id, text));
        if (irc.ReportTarget != null && context.Agent.Config.Irc?.ReportChannel == null)
            irc.Report(text); // no report channel: the event above is not posted, post to the first channel
        return Task.FromResult(ToolResult.Ok("Reported."));
    }
}
