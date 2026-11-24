using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Racka.Core.Config;

namespace Racka.Chat.Slack;

/// <summary>
/// Slack app over Socket Mode: events come over a websocket the app opens, so no public URL is needed.
/// Slack spreads events across the open connections of an app, so every agent needs its own app.
/// </summary>
public sealed partial class SlackConnector : ChatConnectorBase
{
    private const int MaxMessageLength = 3900;
    private static readonly HashSet<string> FatalErrors = ["invalid_auth", "not_authed", "account_inactive", "token_revoked", "not_allowed_token_type"];
    private readonly SlackConfig _config;
    private readonly string _agentName;
    private readonly string _botToken;
    private readonly string _appToken;
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly Queue<string> _recent = new();
    private string? _botUserId;
    private volatile bool _connected;

    public SlackConnector(SlackConfig config, string agentName, ILoggerFactory loggerFactory, HttpMessageHandler? handler = null)
    {
        _config = config;
        _agentName = agentName;
        _logger = loggerFactory.CreateLogger($"racka.{agentName}.slack");
        _botToken = Environment.GetEnvironmentVariable(config.BotTokenEnv) is { Length: > 0 } bot
            ? bot
            : throw new ConfigException($"Environment variable '{config.BotTokenEnv}' (slack.botTokenEnv) is not set.");
        _appToken = Environment.GetEnvironmentVariable(config.AppTokenEnv) is { Length: > 0 } app
            ? app
            : throw new ConfigException($"Environment variable '{config.AppTokenEnv}' (slack.appTokenEnv) is not set.");
        _http = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: true) { BaseAddress = new Uri("https://slack.com/api/") };
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
                _botUserId ??= (await CallAsync(_botToken, "auth.test", null, ct)).GetProperty("user_id").GetString();
                var url = (await CallAsync(_appToken, "apps.connections.open", null, ct)).GetProperty("url").GetString()!;
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri(url), ct);
                var helloSeen = false;
                await ReceiveLoopAsync(socket, () =>
                {
                    helloSeen = true;
                    delay = TimeSpan.FromSeconds(2);
                }, ct);
                if (helloSeen)
                    continue; // Slack asked us to reconnect (it does every few hours): open a new socket right away
                _logger.LogWarning("Slack closed the socket before it was ready");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (SlackApiException e) when (FatalErrors.Contains(e.Error))
            {
                throw new ConfigException($"Slack rejected the token: {e.Error}. Check {_config.BotTokenEnv} (xoxb-…) and {_config.AppTokenEnv} (xapp-… with connections:write).");
            }
            catch (Exception e)
            {
                _logger.LogWarning("Slack connection failed: {Message}", e.Message);
            }
            finally
            {
                _connected = false;
            }
            if (ct.IsCancellationRequested)
                return;
            _logger.LogInformation("Reconnecting to Slack in {Delay}s", delay.TotalSeconds);
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, Action onHello, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var frame = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            frame.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                frame.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            using var doc = JsonDocument.Parse(frame.ToArray());
            var envelope = doc.RootElement;
            if (envelope.TryGetProperty("envelope_id", out var envelopeId))
            {
                var ack = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, string?> { ["envelope_id"] = envelopeId.GetString() }));
                await socket.SendAsync(ack, WebSocketMessageType.Text, true, ct);
            }
            switch (envelope.GetProperty("type").GetString())
            {
                case "hello":
                    _connected = true;
                    onHello();
                    _logger.LogInformation("Connected to Slack (Socket Mode) as {User}", _botUserId);
                    break;
                case "disconnect":
                    _logger.LogInformation("Slack asked to reconnect ({Reason})", envelope.TryGetProperty("reason", out var r) ? r.GetString() : "");
                    return;
                case "events_api" when envelope.TryGetProperty("payload", out var payload) && payload.TryGetProperty("event", out var ev):
                    if (ToChatMessage(ev, _botUserId ?? "") is { } message && IsNew(ev) && (message.IsPrivate || IsListenedChannel(message.ReplyTo.ChannelId)))
                        Raise(message);
                    break;
            }
        }
    }

    /// <summary>Maps a message or app_mention event to a chat message; null for bots, edits and other noise.</summary>
    internal static ChatMessage? ToChatMessage(JsonElement ev, string botUserId)
    {
        var type = ev.GetProperty("type").GetString();
        if (type is not ("message" or "app_mention"))
            return null;
        if (ev.TryGetProperty("subtype", out var subtype) && subtype.GetString() is not ("file_share" or "thread_broadcast"))
            return null;
        if (ev.TryGetProperty("bot_id", out _) || !ev.TryGetProperty("user", out var userElement) || !ev.TryGetProperty("channel", out var channelElement))
            return null;
        var user = userElement.GetString()!;
        var channel = channelElement.GetString()!;
        if (user == botUserId)
            return null;

        var text = ev.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        var mention = $"<@{botUserId}>";
        var mentionsMe = type == "app_mention" || (botUserId.Length > 0 && text.Contains(mention, StringComparison.Ordinal));
        if (botUserId.Length > 0)
            text = text.Replace(mention, "", StringComparison.Ordinal);
        text = Unescape(text).Trim();

        var channelType = ev.TryGetProperty("channel_type", out var ct) ? ct.GetString() : null;
        var isPrivate = channelType == "im" || (channelType == null && channel.StartsWith('D'));
        var thread = ev.TryGetProperty("thread_ts", out var ts) ? ts.GetString() : null;
        return new ChatMessage(new ChatAddress(channel, thread), user, user, text, isPrivate, isPrivate ? user : channel, mentionsMe);
    }

    /// <summary>A message that mentions the bot arrives twice (message and app_mention); handle it once.</summary>
    private bool IsNew(JsonElement ev)
    {
        var key = $"{(ev.TryGetProperty("channel", out var c) ? c.GetString() : "")}/{(ev.TryGetProperty("ts", out var ts) ? ts.GetString() : "")}";
        lock (_recent)
        {
            if (_recent.Contains(key))
                return false;
            _recent.Enqueue(key);
            if (_recent.Count > 200)
                _recent.Dequeue();
            return true;
        }
    }

    public override string AddressOf(ChatMessage message) => $"<@{message.SenderId}> ";

    public override async Task SendAsync(ChatAddress to, string text, CancellationToken ct)
    {
        foreach (var chunk in ChatText.Chunk(Escape(text), MaxMessageLength))
        {
            var body = new Dictionary<string, string> { ["channel"] = to.ChannelId, ["text"] = chunk };
            if (to.ThreadId != null)
                body["thread_ts"] = to.ThreadId;
            await CallAsync(_botToken, "chat.postMessage", body, ct);
        }
    }

    private async Task<JsonElement> CallAsync(string token, string method, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, method)
        {
            Content = body == null ? null : JsonContent.Create(body)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement.Clone();
        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new SlackApiException(method, root.TryGetProperty("error", out var error) ? error.GetString() ?? "unknown" : "unknown");
        return root;
    }

    [GeneratedRegex(@"&lt;@([UW][A-Z0-9]+)&gt;")]
    private static partial Regex EscapedUserMention();

    /// <summary>Slack wants &amp;, &lt; and &gt; escaped; user mentions (&lt;@U…&gt;) stay mentions.</summary>
    internal static string Escape(string text) =>
        EscapedUserMention().Replace(text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"), "<@$1>");

    private static string Unescape(string text) => text.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");

    public override ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class SlackApiException(string method, string error) : Exception($"Slack {method} failed: {error}")
{
    public string Error { get; } = error;
}
