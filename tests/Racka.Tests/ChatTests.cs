using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Racka.Chat;
using Racka.Chat.Discord;
using Racka.Chat.Slack;
using Racka.Chat.Telegram;
using Racka.Core.Config;
using Racka.Core.Engine;
using Racka.Core.Engine.ApiLoop;
using Racka.Core.Llm;
using Racka.Core.Runtime;

namespace Racka.Tests;

public class ChatServiceTests
{
    /// <summary>A chat platform that records what the service sends.</summary>
    private sealed class FakeConnector(ChatConfig config) : ChatConnectorBase
    {
        public BlockingCollection<(string Channel, string Text)> Sent { get; } = new();
        public override ChatConfig Config => config;
        public override bool IsConnected => true;
        public override string Name => "dolly";
        public override Task RunAsync(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);

        public override Task SendAsync(ChatAddress to, string text, CancellationToken ct)
        {
            Sent.Add((to.ChannelId + (to.ThreadId is null ? "" : "/" + to.ThreadId), text));
            return Task.CompletedTask;
        }

        public void Receive(string text, string sender = "42", string channel = "c1", bool isPrivate = false, bool mention = false, string? thread = null) =>
            Raise(new ChatMessage(new ChatAddress(channel, thread), sender, "k0zi", text, isPrivate, isPrivate ? sender : channel, mention));

        public (string Channel, string Text) Next() =>
            Sent.TryTake(out var item, TimeSpan.FromSeconds(10)) ? item : throw new TimeoutException("nothing was sent");
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
        private readonly Task _run;

        public Harness(ChatConfig config, Func<FakeConversation, LlmTurn>? script = null)
        {
            Connector = new FakeConnector(config);
            Events = new AgentEventHub();
            var agent = TestAgents.Create();
            Provider = new FakeLlmProvider(script ?? (conv => FakeLlmProvider.Text($"answer {conv.Messages.Count}")));
            Engine = new ApiLoopEngine(agent, Provider, [], Events, new PromptOptions(false, false, ["fake"]));
            Runtime = new AgentRuntime(agent, Engine, Events);
            Service = new ChatService([Connector], Events, NullLogger.Instance);
            Service.Attach(Runtime);
            _run = Runtime.RunAsync(_stop.Token);
        }

        public FakeConnector Connector { get; }
        public AgentEventHub Events { get; }
        public FakeLlmProvider Provider { get; }
        public ApiLoopEngine Engine { get; }
        public AgentRuntime Runtime { get; }
        public ChatService Service { get; }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _run.WaitAsync(TimeSpan.FromSeconds(5));
            await Service.DisposeAsync();
        }
    }

    private static TelegramConfig Config(params string[] operators) => new() { Operators = [.. operators] };

    [Fact]
    public async Task OperatorTasksAreAnsweredWhereTheyCameFrom()
    {
        await using var h = new Harness(Config("42"));

        h.Connector.Receive("dolly: count the sheep", thread: "t1");
        Assert.Equal(("c1/t1", "k0zi: answer 1"), h.Connector.Next());

        h.Connector.Receive("count again", isPrivate: true, channel: "dm42");
        Assert.Equal(("dm42", "answer 1"), h.Connector.Next()); // private chats have their own history

        h.Connector.Receive("and now?", mention: true);
        Assert.Equal(("c1", "k0zi: answer 3"), h.Connector.Next()); // the channel history continues
    }

    [Fact]
    public async Task OthersOnlyGetStatusAndHelp()
    {
        await using var h = new Harness(Config("42"));

        h.Connector.Receive("dolly: rm -rf /", sender: "666");
        Assert.Equal(("c1", "k0zi: Sorry, only operators can give me tasks or commands."), h.Connector.Next());
        Assert.Equal(0, h.Provider.Calls);

        h.Connector.Receive("!status", sender: "666");
        Assert.StartsWith("k0zi: dolly [anthropic] idle", h.Connector.Next().Text);

        h.Connector.Receive("!help", sender: "666");
        Assert.Contains("!status | !stop | !reset", h.Connector.Next().Text);

        h.Connector.Receive("hello everyone", sender: "666");
        h.Connector.Receive("!status", sender: "666");
        Assert.Contains("idle", h.Connector.Next().Text); // the chatter got no answer
    }

    [Fact]
    public async Task ResetStartsTheConversationOver()
    {
        await using var h = new Harness(Config("42"));
        h.Connector.Receive("dolly: one");
        Assert.Equal("k0zi: answer 1", h.Connector.Next().Text);
        h.Connector.Receive("dolly: two");
        Assert.Equal("k0zi: answer 3", h.Connector.Next().Text);

        h.Connector.Receive("!reset");
        Assert.Equal("k0zi: conversation reset.", h.Connector.Next().Text);
        h.Connector.Receive("dolly: three");
        Assert.Equal("k0zi: answer 1", h.Connector.Next().Text);
    }

    [Fact]
    public async Task LongAnswersAreCut()
    {
        var config = Config("42");
        config.MaxReplyLines = 3;
        await using var h = new Harness(config, _ => FakeLlmProvider.Text(string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line {i}"))));

        h.Connector.Receive("dolly: list");
        var reply = h.Connector.Next().Text;
        Assert.StartsWith("k0zi: line 1\nline 2\nline 3\n… 7 more lines", reply);
    }

    [Fact]
    public async Task EventsGoToTheReportChannelAndChatReportFallsBackToTheFirstChannel()
    {
        var config = Config("42");
        config.Channels = ["first"];
        await using var h = new Harness(config);

        h.Events.Publish(new AgentEvent(AgentEventType.TaskStarted, "dolly", "t1", "work"));
        h.Events.Publish(new AgentEvent(AgentEventType.Report, "dolly", "t1", "halfway there"));
        Assert.Equal(("first", "halfway there"), h.Connector.Next()); // no report channel: no lifecycle events

        config.ReportChannel = "status";
        h.Events.Publish(new AgentEvent(AgentEventType.TaskCompleted, "dolly", "t1", "done"));
        Assert.Equal(("status", "✔ t1: done"), h.Connector.Next());
    }

    [Fact]
    public async Task ChatReportToolNeedsAReportTarget()
    {
        await using var h = new Harness(Config("42"));
        var tool = new ChatReportTool(h.Service);
        var context = new Racka.Core.Tools.ToolContext(h.Runtime.Agent, AgentTask.Create("x", TaskSources.Cli), h.Events);

        var result = await tool.InvokeAsync(JsonDocument.Parse("""{"text":"hi"}""").RootElement, context, CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Contains("report channel", result.Content);
    }

    [Theory]
    [InlineData("count the sheep", true, ChatCommandKind.Task, "count the sheep")]
    [InlineData("status", true, ChatCommandKind.Status, "")]
    [InlineData("!help", true, ChatCommandKind.Help, "")]
    [InlineData("count the sheep", false, null, "")]
    public void MentionsAddressTheBot(string text, bool mention, ChatCommandKind? kind, string task)
    {
        var command = ChatCommandParser.Parse(text, "dolly", "!", isPrivate: false, mentionsMe: mention);
        Assert.Equal(kind, command?.Kind);
        if (command != null)
            Assert.Equal(task, command.Text);
    }

    [Fact]
    public void ChunksAtLinesAndNeverSplitsSurrogates()
    {
        var chunks = ChatText.Chunk("aaaa\nbbbb\ncccc", 9);
        Assert.Equal(["aaaa\nbbbb", "cccc"], chunks);

        var emoji = ChatText.Chunk(string.Concat(Enumerable.Repeat("🐑", 10)), 5);
        Assert.All(emoji, c => Assert.False(char.IsHighSurrogate(c[^1])));
        Assert.Equal(20, emoji.Sum(c => c.Length));
    }
}

public class ChatPlatformTests
{
    private static string SetSecret(string value)
    {
        var name = "RACKA_TEST_" + Guid.NewGuid().ToString("n")[..8].ToUpperInvariant();
        Environment.SetEnvironmentVariable(name, value);
        return name;
    }

    [Theory]
    [InlineData("/status", "!status")]
    [InlineData("/stop@racka_bot", "!stop")]
    [InlineData("/reset@Racka_Bot now", "!reset now")]
    [InlineData("/start", "!help")]
    [InlineData("/stop@other_bot", null)]
    [InlineData("/weather today", "/weather today")]
    [InlineData("count the sheep", "count the sheep")]
    public void TelegramCommandsBecomeRackaCommands(string text, string? expected) =>
        Assert.Equal(expected, TelegramConnector.NormalizeCommand(text, "racka_bot", "!"));

    [Fact]
    public async Task TelegramPollsMessagesAndSendsReplies()
    {
        var handler = new TelegramApi(
            """{"ok":true,"result":{"id":7,"is_bot":true,"first_name":"Dolly","username":"dolly_bot"}}""",
            """
            {"ok":true,"result":[
              {"update_id":10,"message":{"message_id":1,"date":0,"chat":{"id":-100,"type":"supergroup"},"from":{"id":42,"is_bot":false,"first_name":"K","username":"k0zi"},"text":"@dolly_bot count the sheep"}},
              {"update_id":11,"message":{"message_id":2,"date":0,"chat":{"id":-100,"type":"supergroup"},"from":{"id":43,"is_bot":false,"first_name":"Z"},"text":"just chatting"}},
              {"update_id":12,"message":{"message_id":3,"date":0,"chat":{"id":42,"type":"private"},"from":{"id":42,"is_bot":false,"first_name":"K","username":"k0zi"},"text":"/status"}}
            ]}
            """);
        var config = new TelegramConfig { TokenEnv = SetSecret("123:abc") };
        await using var telegram = new TelegramConnector(config, "dolly", NullLoggerFactory.Instance, new HttpClient(handler));
        var received = new BlockingCollection<ChatMessage>();
        telegram.MessageReceived += received.Add;

        using var stop = new CancellationTokenSource();
        var run = telegram.RunAsync(stop.Token);
        var mention = received.Take(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        var chatter = received.Take(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        var command = received.Take(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

        Assert.Equal(new ChatMessage(new ChatAddress("-100"), "42", "k0zi", "count the sheep", false, "-100", true), mention);
        Assert.False(chatter.MentionsMe);
        Assert.Equal(new ChatMessage(new ChatAddress("42"), "42", "k0zi", "!status", true, "42"), command);
        Assert.Equal("@k0zi ", telegram.AddressOf(mention));

        await telegram.SendAsync(new ChatAddress("-100"), "done", CancellationToken.None);
        var body = Assert.Single(handler.Sent);
        Assert.Equal(-100, body.GetProperty("chat_id").GetInt64());
        Assert.Equal("done", body.GetProperty("text").GetString());
        Assert.Equal(13, handler.LastOffset); // the next poll acknowledges updates 10–12
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Bot API stand-in: getMe, one batch of updates, then long polls that wait; records sendMessage.</summary>
    private sealed class TelegramApi(string me, string updates) : HttpMessageHandler
    {
        private int _polls;
        public List<JsonElement> Sent { get; } = new();
        public long? LastOffset { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? "{}" : await request.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
            var json = request.RequestUri!.AbsolutePath.Split('/')[^1] switch
            {
                "getMe" => me,
                "getUpdates" when Interlocked.Increment(ref _polls) == 1 => updates,
                "getUpdates" => await WaitAsync(doc.RootElement, ct),
                "sendMessage" => Record(doc.RootElement),
                var other => throw new InvalidOperationException(other)
            };
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        }

        private async Task<string> WaitAsync(JsonElement request, CancellationToken ct)
        {
            LastOffset = request.TryGetProperty("offset", out var offset) ? offset.GetInt64() : null;
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        }

        private string Record(JsonElement request)
        {
            Sent.Add(request.Clone());
            return """{"ok":true,"result":{"message_id":9,"date":0,"chat":{"id":-100,"type":"supergroup"},"text":"x"}}""";
        }
    }

    [Fact]
    public void TelegramNeedsItsToken()
    {
        var e = Assert.Throws<ConfigException>(() => new TelegramConnector(new TelegramConfig { TokenEnv = "RACKA_TEST_MISSING" }, "dolly", NullLoggerFactory.Instance));
        Assert.Contains("RACKA_TEST_MISSING", e.Message);
    }

    [Fact]
    public void SlackEventsBecomeChatMessages()
    {
        ChatMessage? Map(string json) => SlackConnector.ToChatMessage(JsonDocument.Parse(json).RootElement, "UBOT");

        Assert.Equal(new ChatMessage(new ChatAddress("C1", "1700.1"), "U42", "U42", "count <the> sheep & goats", false, "C1", true),
            Map("""{"type":"app_mention","user":"U42","channel":"C1","thread_ts":"1700.1","text":"<@UBOT> count &lt;the&gt; sheep &amp; goats","ts":"1700.2"}"""));
        Assert.Equal(new ChatMessage(new ChatAddress("D9"), "U42", "U42", "hi", true, "U42"),
            Map("""{"type":"message","user":"U42","channel":"D9","channel_type":"im","text":"hi","ts":"1"}"""));
        Assert.False(Map("""{"type":"message","user":"U42","channel":"C1","channel_type":"channel","text":"dolly: hi","ts":"1"}""")!.MentionsMe);
        Assert.Null(Map("""{"type":"message","subtype":"message_changed","channel":"C1","ts":"1"}"""));
        Assert.Null(Map("""{"type":"message","bot_id":"B1","user":"U9","channel":"C1","text":"hi","ts":"1"}"""));
        Assert.Null(Map("""{"type":"message","user":"UBOT","channel":"C1","text":"my own","ts":"1"}"""));
        Assert.Null(Map("""{"type":"reaction_added","user":"U42","item":{}}"""));
    }

    [Fact]
    public async Task SlackPostsEscapedRepliesInTheThread()
    {
        var handler = new FakeHttpHandler("""{"ok":true}""", """{"ok":false,"error":"channel_not_found"}""");
        var config = new SlackConfig { BotTokenEnv = SetSecret("xoxb-1"), AppTokenEnv = SetSecret("xapp-1") };
        await using var slack = new SlackConnector(config, "dolly", NullLoggerFactory.Instance, handler);

        await slack.SendAsync(new ChatAddress("C1", "1700.1"), "<@U42> a < b && c", CancellationToken.None);
        var (request, body) = handler.Requests.Single();
        Assert.Equal("https://slack.com/api/chat.postMessage", request.RequestUri!.ToString());
        Assert.Equal("Bearer xoxb-1", request.Headers.Authorization!.ToString());
        Assert.Equal("<@U42> a &lt; b &amp;&amp; c", body.GetProperty("text").GetString());
        Assert.Equal("1700.1", body.GetProperty("thread_ts").GetString());

        var e = await Assert.ThrowsAnyAsync<Exception>(() => slack.SendAsync(new ChatAddress("C2"), "x", CancellationToken.None));
        Assert.Contains("channel_not_found", e.Message);
    }

    [Theory]
    [InlineData("<@7> count the sheep", "count the sheep")]
    [InlineData("hey <@!7>, count", "hey , count")]
    [InlineData("ask <@8> too", "ask <@8> too")]
    public void DiscordMentionsOfTheBotAreStripped(string content, string expected) =>
        Assert.Equal(expected, DiscordConnector.StripMention(content, 7));
}
