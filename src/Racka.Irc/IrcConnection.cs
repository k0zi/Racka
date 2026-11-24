using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Racka.Irc;

public sealed record IrcConnectionOptions(
    string Server,
    int Port,
    bool Tls,
    string Nick,
    string User,
    string RealName,
    IReadOnlyList<string> Channels,
    string? SaslPassword = null,
    TimeSpan? SendInterval = null,
    int SendBurst = 4);

/// <summary>
/// A minimal IRC client: registration (optionally with SASL PLAIN), PING/PONG, auto-join,
/// nick collision handling, flood-protected sending and reconnect with exponential backoff.
/// </summary>
public sealed class IrcConnection
{
    private readonly IrcConnectionOptions _options;
    private readonly ILogger _logger;
    private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private StreamWriter? _writer;
    private volatile bool _registered;

    public IrcConnection(IrcConnectionOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
        CurrentNick = options.Nick;
    }

    public event Action<IrcMessage>? MessageReceived;
    public event Action? Registered;

    public bool IsConnected => _registered;
    public string CurrentNick { get; private set; }

    public void SendRaw(string line) => _outgoing.Writer.TryWrite(line);

    public void SendMessage(string target, string text, bool notice = false)
    {
        var command = notice ? "NOTICE" : "PRIVMSG";
        foreach (var line in IrcText.SplitLines(text))
            SendRaw($"{command} {target} :{line}");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(ct);
                delay = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogWarning("IRC connection to {Server} failed: {Message}", _options.Server, e.Message);
            }
            _registered = false;
            if (ct.IsCancellationRequested)
                return;
            _logger.LogInformation("Reconnecting to IRC in {Delay}s", delay.TotalSeconds);
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
        }
    }

    private async Task RunSessionAsync(CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(_options.Server, _options.Port, ct);
        Stream stream = tcp.GetStream();
        if (_options.Tls)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = _options.Server }, ct);
            stream = ssl;
        }
        await using var _ = stream;
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Registration and protocol replies bypass the flood queue.
        CurrentNick = _options.Nick;
        if (_options.SaslPassword != null)
            await WriteAsync("CAP REQ :sasl");
        await WriteAsync($"NICK {CurrentNick}");
        await WriteAsync($"USER {_options.User} 0 * :{_options.RealName}");

        var sender = SendLoopAsync(session.Token);
        try
        {
            while (!session.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(session.Token);
                if (line == null)
                    throw new IOException("Server closed the connection.");
                var message = IrcMessage.Parse(line);
                if (message != null)
                    await HandleAsync(message);
            }
        }
        finally
        {
            _registered = false;
            await session.CancelAsync();
            try { await sender; } catch (OperationCanceledException) { }
        }
    }

    private async Task HandleAsync(IrcMessage message)
    {
        switch (message.Command)
        {
            case "PING":
                await WriteAsync($"PONG :{message.Trailing}");
                return;
            case "CAP" when message.Parameters.Count >= 2 && message.Parameters[1] == "ACK":
                await WriteAsync("AUTHENTICATE PLAIN");
                return;
            case "CAP" when message.Parameters.Count >= 2 && message.Parameters[1] == "NAK":
                _logger.LogWarning("IRC server does not support SASL");
                await WriteAsync("CAP END");
                return;
            case "AUTHENTICATE" when message.Trailing == "+":
                var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.Nick}\0{_options.Nick}\0{_options.SaslPassword}"));
                await WriteAsync($"AUTHENTICATE {payload}");
                return;
            case "903": // SASL success
                await WriteAsync("CAP END");
                return;
            case "904" or "905" or "906":
                _logger.LogWarning("IRC SASL authentication failed: {Reason}", message.Trailing);
                await WriteAsync("CAP END");
                return;
            case "433": // nick in use
                if (!_registered)
                {
                    CurrentNick += "_";
                    await WriteAsync($"NICK {CurrentNick}");
                }
                return;
            case "001":
                CurrentNick = message.Parameters.Count > 0 ? message.Parameters[0] : CurrentNick;
                _registered = true;
                _logger.LogInformation("Connected to IRC {Server} as {Nick}", _options.Server, CurrentNick);
                foreach (var channel in _options.Channels)
                    await WriteAsync($"JOIN {channel}");
                Registered?.Invoke();
                return;
            case "NICK" when string.Equals(message.Nick, CurrentNick, StringComparison.OrdinalIgnoreCase):
                CurrentNick = message.Trailing ?? CurrentNick;
                return;
            case "ERROR":
                throw new IOException($"Server error: {message.Trailing}");
        }
        MessageReceived?.Invoke(message);
    }

    private async Task WriteAsync(string line)
    {
        await _writeLock.WaitAsync();
        try { await _writer!.WriteLineAsync(line); }
        finally { _writeLock.Release(); }
    }

    /// <summary>Token bucket: up to SendBurst lines at once, then one line per SendInterval.</summary>
    private async Task SendLoopAsync(CancellationToken ct)
    {
        var interval = _options.SendInterval ?? TimeSpan.FromSeconds(1);
        double tokens = _options.SendBurst;
        var last = DateTime.UtcNow;
        await foreach (var line in _outgoing.Reader.ReadAllAsync(ct))
        {
            while (!_registered)
                await Task.Delay(200, ct);
            var now = DateTime.UtcNow;
            tokens = Math.Min(_options.SendBurst, tokens + (now - last) / interval);
            last = now;
            if (tokens < 1)
            {
                await Task.Delay(interval * (1 - tokens), ct);
                tokens = 1;
                last = DateTime.UtcNow;
            }
            tokens -= 1;
            await WriteAsync(line);
        }
    }
}
