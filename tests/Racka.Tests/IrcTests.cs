using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Racka.Core.Config;
using Racka.Core.Engine;
using Racka.Core.Runtime;
using Racka.Irc;

namespace Racka.Tests;

public class IrcTests
{
    [Fact]
    public void ParsesProtocolLines()
    {
        var m = IrcMessage.Parse("@time=x :k0zi!~k@host.example PRIVMSG #racka :dolly: count the files\r\n")!;
        Assert.Equal("k0zi!~k@host.example", m.Prefix);
        Assert.Equal("k0zi", m.Nick);
        Assert.Equal("PRIVMSG", m.Command);
        Assert.Equal(["#racka", "dolly: count the files"], m.Parameters);

        var ping = IrcMessage.Parse("PING :abc")!;
        Assert.Null(ping.Prefix);
        Assert.Equal("abc", ping.Trailing);

        var welcome = IrcMessage.Parse(":server 001 dolly :Welcome")!;
        Assert.Equal(["dolly", "Welcome"], welcome.Parameters);
    }

    [Theory]
    [InlineData("dolly: count files", false, IrcCommandKind.Task, "count files")]
    [InlineData("Dolly, count files", false, IrcCommandKind.Task, "count files")]
    [InlineData("!dolly count files", false, IrcCommandKind.Task, "count files")]
    [InlineData("!status", false, IrcCommandKind.Status, "")]
    [InlineData("!stop dolly", false, IrcCommandKind.Stop, "")]
    [InlineData("dolly: reset", false, IrcCommandKind.Reset, "")]
    [InlineData("!help", false, IrcCommandKind.Help, "")]
    [InlineData("just do it", true, IrcCommandKind.Task, "just do it")]
    [InlineData("!status", true, IrcCommandKind.Status, "")]
    public void ParsesCommandsForMe(string text, bool isPrivate, IrcCommandKind kind, string task)
    {
        var command = IrcCommandParser.Parse(text, "dolly", "!", isPrivate);
        Assert.NotNull(command);
        Assert.Equal(kind, command.Kind);
        Assert.Equal(task, command.Text);
    }

    [Theory]
    [InlineData("shaun: count files")]
    [InlineData("!shaun count files")]
    [InlineData("!stop shaun")]
    [InlineData("hello everyone")]
    [InlineData("dolly:")]
    public void IgnoresMessagesForOthers(string text) =>
        Assert.Null(IrcCommandParser.Parse(text, "dolly", "!", isPrivate: false));

    [Theory]
    [InlineData("k0zi!~k@host.example.net", "k0zi!*@*.example.net", true)]
    [InlineData("K0ZI!~k@host.example.net", "k0zi!*@*", true)]
    [InlineData("evil!~k@host.example.net", "k0zi!*@*", false)]
    [InlineData("k0zi!~k@evil.org", "k0zi!*@*.example.net", false)]
    [InlineData("k0zi!~k@host", "k0zi!?k@host", true)]
    public void MatchesHostmasks(string prefix, string mask, bool expected) =>
        Assert.Equal(expected, IrcText.MatchesMask(prefix, mask));

    [Fact]
    public void SplitsLongTextAtWordsAndByteLimit()
    {
        var text = string.Join(' ', Enumerable.Repeat("bárány", 200)) + "\n\nsecond\tline\u0007";
        var lines = IrcText.SplitLines(text, 100);

        Assert.All(lines, l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 100));
        Assert.All(lines.Take(lines.Count - 1), l => Assert.EndsWith("bárány", l));
        Assert.Equal("second line", lines[^1]);
    }

    [Fact]
    public void SplitsEmojiWithoutBreakingSurrogates()
    {
        var lines = IrcText.SplitLines(new string('x', 3) + string.Concat(Enumerable.Repeat("🐑", 10)), 10);
        Assert.All(lines, l => Assert.False(char.IsHighSurrogate(l[^1])));
        Assert.Equal(3 + 20, lines.Sum(l => l.Length));
    }

    [Fact]
    public async Task TakesTasksFromOperatorsAndRepliesInChannel()
    {
        using var server = new FakeIrcServer();
        var config = new IrcConfig
        {
            Server = "127.0.0.1", Port = server.Port, Tls = false, Nick = "dolly",
            Channels = ["#racka"], ReportChannel = "#status", Operators = ["k0zi!*@*"]
        };
        var events = new AgentEventHub();
        await using var irc = new IrcService(config, "dolly", events, NullLoggerFactory.Instance);
        var provider = new FakeLlmProvider(conv => FakeLlmProvider.Text($"I counted {conv.Messages.Count} message(s)."));
        var agent = TestAgents.Create();
        var runtime = new AgentRuntime(agent, new Racka.Core.Engine.ApiLoop.ApiLoopEngine(agent, provider, [], events, new PromptOptions(false, false, true)), events);
        irc.Attach(runtime);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var runtimeTask = runtime.RunAsync(stop.Token);
        var ircTask = irc.RunAsync(stop.Token);

        await server.AcceptAsync();
        Assert.Equal("NICK dolly", await server.ReadLineAsync());
        Assert.StartsWith("USER dolly", await server.ReadLineAsync());
        await server.SendAsync(":irc.test 001 dolly :Welcome");
        Assert.Equal("JOIN #racka", await server.ReadLineAsync());
        Assert.Equal("JOIN #status", await server.ReadLineAsync());
        Assert.Equal("PRIVMSG #status :online (anthropic engine ready)", await server.ReadUntilAsync(l => l.Contains("online")));

        await server.SendAsync("PING :keepalive");
        Assert.Equal("PONG :keepalive", await server.ReadLineAsync());

        await server.SendAsync(":evil!~e@bad.host PRIVMSG #racka :dolly: rm -rf /");
        Assert.Equal("NOTICE evil :Sorry, only operators can give me tasks or commands.", await server.ReadLineAsync());

        await server.SendAsync(":k0zi!~k@home PRIVMSG #racka :dolly: how many?");
        var reply = await server.ReadUntilAsync(l => l.StartsWith("PRIVMSG #racka"));
        Assert.Equal("PRIVMSG #racka :k0zi: I counted 1 message(s).", reply);

        await server.SendAsync(":anyone!~a@x PRIVMSG #racka :!status");
        var status = await server.ReadUntilAsync(l => l.StartsWith("PRIVMSG #racka"));
        Assert.Contains("dolly [anthropic] idle", status);
        Assert.Contains("done 1", status);

        await stop.CancelAsync();
        await Task.WhenAll(runtimeTask, ircTask).WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>A single-client line based TCP server standing in for an IRC server.</summary>
    private sealed class FakeIrcServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private TcpClient? _client;
        private StreamReader? _reader;
        private StreamWriter? _writer;

        public FakeIrcServer() => _listener.Start();

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public async Task AcceptAsync()
        {
            _client = await _listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var stream = _client.GetStream();
            _reader = new StreamReader(stream, new UTF8Encoding(false));
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
        }

        public async Task<string> ReadLineAsync() =>
            await _reader!.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) ?? throw new IOException("closed");

        public async Task<string> ReadUntilAsync(Func<string, bool> predicate)
        {
            while (true)
            {
                var line = await ReadLineAsync();
                if (predicate(line))
                    return line;
            }
        }

        public Task SendAsync(string line) => _writer!.WriteLineAsync(line);

        public void Dispose()
        {
            _client?.Dispose();
            _listener.Stop();
        }
    }
}
