using System.Globalization;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Racka.Core.Config;
using DiscordConfig = Racka.Core.Config.DiscordConfig;

namespace Racka.Chat.Discord;

/// <summary>
/// Discord bot over the gateway. The bot needs the Message Content intent (Developer Portal → Bot) to read
/// messages that do not mention it; Discord.Net reconnects on its own.
/// </summary>
public sealed partial class DiscordConnector : ChatConnectorBase
{
    private const int MaxMessageLength = 2000;
    private readonly DiscordConfig _config;
    private readonly string _agentName;
    private readonly string _token;
    private readonly ILogger _logger;
    private readonly DiscordSocketClient _client;

    public DiscordConnector(DiscordConfig config, string agentName, ILoggerFactory loggerFactory)
    {
        _config = config;
        _agentName = agentName;
        _logger = loggerFactory.CreateLogger($"racka.{agentName}.discord");
        _token = Environment.GetEnvironmentVariable(config.TokenEnv) is { Length: > 0 } token
            ? token
            : throw new ConfigException($"Environment variable '{config.TokenEnv}' (discord.tokenEnv) is not set.");
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.DirectMessages | GatewayIntents.MessageContent,
            LogLevel = LogSeverity.Info
        });
        _client.Log += OnLog;
        _client.Ready += () =>
        {
            _logger.LogInformation("Connected to Discord as {User}", _client.CurrentUser?.Username);
            return Task.CompletedTask;
        };
        _client.MessageReceived += OnMessageAsync;
    }

    public override ChatConfig Config => _config;
    public override bool IsConnected => _client.ConnectionState == ConnectionState.Connected && _client.CurrentUser != null;
    public override string Name => _agentName;

    public override async Task RunAsync(CancellationToken ct)
    {
        await _client.LoginAsync(TokenType.Bot, _token);
        await _client.StartAsync();
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            await _client.StopAsync();
            await _client.LogoutAsync();
        }
    }

    private Task OnMessageAsync(SocketMessage message)
    {
        if (message is not SocketUserMessage { Author.IsBot: false } userMessage || _client.CurrentUser is not { } me)
            return Task.CompletedTask;
        var channel = userMessage.Channel;
        var isPrivate = channel is IPrivateChannel;
        var channelId = channel.Id.ToString(CultureInfo.InvariantCulture);
        if (!isPrivate && !IsListenedChannel(channelId, channel.Name)
                       && !(channel is SocketThreadChannel { ParentChannel: { } parent } && IsListenedChannel(parent.Id.ToString(CultureInfo.InvariantCulture), parent.Name)))
            return Task.CompletedTask;

        var mentionsMe = userMessage.MentionedUsers.Any(u => u.Id == me.Id) || userMessage.ReferencedMessage?.Author.Id == me.Id;
        var authorId = userMessage.Author.Id.ToString(CultureInfo.InvariantCulture);
        Raise(new ChatMessage(
            new ChatAddress(channelId),
            authorId,
            userMessage.Author.GlobalName ?? userMessage.Author.Username,
            StripMention(userMessage.Content, me.Id),
            isPrivate,
            isPrivate ? authorId : channelId,
            mentionsMe));
        return Task.CompletedTask;
    }

    [GeneratedRegex(@"<@!?(\d+)>")]
    private static partial Regex UserMention();

    /// <summary>Removes the &lt;@id&gt; / &lt;@!id&gt; mentions of the bot.</summary>
    internal static string StripMention(string content, ulong botId) =>
        UserMention().Replace(content, m => m.Groups[1].Value == botId.ToString(CultureInfo.InvariantCulture) ? "" : m.Value).Trim();

    public override string AddressOf(ChatMessage message) => $"<@{message.SenderId}> ";

    public override async Task SendAsync(ChatAddress to, string text, CancellationToken ct)
    {
        var id = ulong.Parse(to.ChannelId, CultureInfo.InvariantCulture);
        var channel = await _client.GetChannelAsync(id) as IMessageChannel
                      ?? throw new InvalidOperationException($"Discord channel {to.ChannelId} is not a text channel the bot can see.");
        foreach (var chunk in ChatText.Chunk(text, MaxMessageLength))
            await channel.SendMessageAsync(chunk, allowedMentions: new AllowedMentions(AllowedMentionTypes.Users),
                options: new RequestOptions { CancelToken = ct });
    }

    private Task OnLog(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Debug,
            _ => LogLevel.Trace
        };
        _logger.Log(level, message.Exception, "{Source}: {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }

    public override async ValueTask DisposeAsync() => await _client.DisposeAsync();
}
