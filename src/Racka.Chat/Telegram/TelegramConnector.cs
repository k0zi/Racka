using System.Globalization;
using Microsoft.Extensions.Logging;
using Racka.Core.Config;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Racka.Chat.Telegram;

/// <summary>
/// Telegram bot over long polling (no public endpoint needed). Only one process may poll a bot token,
/// so every agent needs its own bot.
/// </summary>
public sealed class TelegramConnector : ChatConnectorBase
{
    private const int MaxMessageLength = 4096;
    private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase) { "status", "stop", "reset", "help" };
    private readonly TelegramConfig _config;
    private readonly string _agentName;
    private readonly ITelegramBotClient _bot;
    private readonly ILogger _logger;
    private User? _me;
    private volatile bool _connected;

    public TelegramConnector(TelegramConfig config, string agentName, ILoggerFactory loggerFactory, HttpClient? httpClient = null)
    {
        _config = config;
        _agentName = agentName;
        _logger = loggerFactory.CreateLogger($"racka.{agentName}.telegram");
        var token = Environment.GetEnvironmentVariable(config.TokenEnv);
        if (string.IsNullOrEmpty(token))
            throw new ConfigException($"Environment variable '{config.TokenEnv}' (telegram.tokenEnv) is not set.");
        _bot = new TelegramBotClient(token, httpClient);
    }

    public override ChatConfig Config => _config;
    public override bool IsConnected => _connected;
    public override string Name => _agentName;

    public override async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _me = await _bot.GetMe(ct);
                _connected = true;
                _logger.LogInformation("Connected to Telegram as @{Username}", _me.Username);
                int? offset = null;
                while (!ct.IsCancellationRequested)
                {
                    var updates = await _bot.GetUpdates(offset, timeout: 50, allowedUpdates: [UpdateType.Message], cancellationToken: ct);
                    delay = TimeSpan.FromSeconds(2);
                    foreach (var update in updates)
                    {
                        offset = update.Id + 1;
                        if (update.Message is { } message)
                            OnMessage(message);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ApiRequestException e) when (e.ErrorCode == 401)
            {
                throw new ConfigException($"Telegram rejected the token in '{_config.TokenEnv}': {e.Message}");
            }
            catch (ApiRequestException e) when (e.ErrorCode == 409)
            {
                _logger.LogError("Telegram: another process is polling this bot, or the bot has a webhook. " +
                                 "Every agent needs its own Telegram bot. ({Message})", e.Message);
            }
            catch (Exception e)
            {
                _logger.LogWarning("Telegram connection failed: {Message}", e.Message);
            }
            _connected = false;
            if (ct.IsCancellationRequested)
                return;
            _logger.LogInformation("Reconnecting to Telegram in {Delay}s", delay.TotalSeconds);
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
        }
    }

    internal void OnMessage(Message message)
    {
        if (message.Text is not { } text || message.From is not { IsBot: false } from || _me == null)
            return;
        var isPrivate = message.Chat.Type == ChatType.Private;
        var chatId = message.Chat.Id.ToString(CultureInfo.InvariantCulture);
        if (!isPrivate && !IsListenedChannel(chatId, message.Chat.Username))
            return;

        if (NormalizeCommand(text, _me.Username, _config.CommandPrefix) is not { } normalized)
            return; // a /command for another bot
        text = normalized;
        var mentionsMe = message.ReplyToMessage?.From?.Id == _me.Id;
        if (_me.Username is { Length: > 0 } username && text.Contains("@" + username, StringComparison.OrdinalIgnoreCase))
        {
            mentionsMe = true;
            text = text.Replace("@" + username, "", StringComparison.OrdinalIgnoreCase).Trim();
        }

        var userId = from.Id.ToString(CultureInfo.InvariantCulture);
        var thread = message.IsTopicMessage && message.MessageThreadId is { } threadId ? threadId.ToString(CultureInfo.InvariantCulture) : null;
        Raise(new ChatMessage(
            new ChatAddress(chatId, thread),
            userId,
            from.Username ?? from.FirstName,
            text,
            isPrivate,
            isPrivate ? userId : chatId,
            mentionsMe));
    }

    /// <summary>
    /// Turns Telegram commands into Racka ones: /status, /stop, /reset and /help (optionally /cmd@thisbot) become
    /// !status…, /start becomes !help. Returns null for a /command addressed to another bot.
    /// </summary>
    internal static string? NormalizeCommand(string text, string? botUsername, string prefix)
    {
        text = text.Trim();
        if (!text.StartsWith('/'))
            return text;
        var space = text.IndexOf(' ');
        var word = space < 0 ? text[1..] : text[1..space];
        var rest = space < 0 ? "" : text[space..];
        var at = word.IndexOf('@');
        if (at >= 0)
        {
            if (!string.Equals(word[(at + 1)..], botUsername, StringComparison.OrdinalIgnoreCase))
                return null;
            word = word[..at];
        }
        if (word.Equals("start", StringComparison.OrdinalIgnoreCase))
            return prefix + "help";
        return Commands.Contains(word) ? prefix + word + rest : text;
    }

    public override string AddressOf(ChatMessage message) =>
        message.SenderName.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? $"@{message.SenderName} " : $"{message.SenderName}: ";

    public override async Task SendAsync(ChatAddress to, string text, CancellationToken ct)
    {
        ChatId chat = long.TryParse(to.ChannelId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? new ChatId(id) : new ChatId(to.ChannelId);
        int? thread = int.TryParse(to.ThreadId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : null;
        foreach (var chunk in ChatText.Chunk(text, MaxMessageLength))
            await _bot.SendMessage(chat, chunk, messageThreadId: thread, cancellationToken: ct);
    }
}
