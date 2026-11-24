using Microsoft.Extensions.Logging;
using Racka.Core.Config;

namespace Racka.Chat.Irc;

/// <summary>IRC: each agent is its own nick; operators are nick!user@host masks.</summary>
public sealed class IrcConnector : IChatConnector
{
    private readonly IrcConfig _config;
    private readonly IrcConnection _connection;

    public IrcConnector(IrcConfig config, string agentName, ILoggerFactory loggerFactory)
    {
        _config = config;
        var password = config.SaslPasswordEnv is { } env ? Environment.GetEnvironmentVariable(env) : null;
        if (config.SaslPasswordEnv != null && string.IsNullOrEmpty(password))
            throw new ConfigException($"Environment variable '{config.SaslPasswordEnv}' (irc.saslPasswordEnv) is not set.");
        var channels = config.Channels.ToList();
        if (config.ReportChannel is { } report && !channels.Contains(report, StringComparer.OrdinalIgnoreCase))
            channels.Add(report);

        _connection = new IrcConnection(new IrcConnectionOptions(
            config.Server, config.Port, config.Tls, config.Nick,
            config.User ?? config.Nick, config.RealName ?? $"Racka agent {agentName}",
            channels, password), loggerFactory.CreateLogger($"racka.{agentName}.irc"));
        _connection.MessageReceived += OnIrcMessage;
    }

    public ChatConfig Config => _config;
    public bool IsConnected => _connection.IsConnected;
    public string Name => _connection.CurrentNick;
    public event Action<ChatMessage>? MessageReceived;

    public Task RunAsync(CancellationToken ct) => _connection.RunAsync(ct);

    public Task SendAsync(ChatAddress to, string text, CancellationToken ct)
    {
        _connection.SendMessage(to.ChannelId, text);
        return Task.CompletedTask;
    }

    public Task SendNoticeAsync(ChatMessage to, string text, CancellationToken ct)
    {
        _connection.SendMessage(to.SenderName, text, notice: true);
        return Task.CompletedTask;
    }

    public bool IsOperator(ChatMessage message) => _config.Operators.Any(mask => IrcText.MatchesMask(message.SenderId, mask));

    public string AddressOf(ChatMessage message) => $"{message.SenderName}: ";

    public IReadOnlyList<string> SplitLines(string text) => IrcText.SplitLines(text);

    private void OnIrcMessage(IrcMessage message)
    {
        if (message.Command != "PRIVMSG" || message.Parameters.Count < 2 || message.Prefix is null || message.Nick is null)
            return;
        var target = message.Parameters[0];
        var text = message.Parameters[1];
        if (text.StartsWith('\u0001'))
            return; // CTCP
        var isPrivate = string.Equals(target, _connection.CurrentNick, StringComparison.OrdinalIgnoreCase);
        var channel = isPrivate ? message.Nick : target;
        MessageReceived?.Invoke(new ChatMessage(new ChatAddress(channel), message.Prefix, message.Nick, text, isPrivate, channel));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
