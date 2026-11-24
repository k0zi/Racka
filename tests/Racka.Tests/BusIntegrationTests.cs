using Racka.Core.Agents;
using Racka.Core.Config;
using Racka.Core.Engine;
using Racka.Core.Engine.ApiLoop;
using Racka.Core.Llm;
using Racka.Core.Memory;
using Racka.Core.Messaging;
using Racka.Core.Skills;
using Racka.Host;

namespace Racka.Tests;

/// <summary>Runs real agent hosts on Unix sockets with scripted model providers.</summary>
public sealed class BusIntegrationTests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task<int>> _hosts = new();
    private BusPaths _paths = null!;
    private BusClient _bus = null!;

    public Task InitializeAsync()
    {
        _paths = new BusPaths(Path.Combine(_dir.Path, "run"));
        _bus = new BusClient(_paths);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _stop.CancelAsync();
        await Task.WhenAll(_hosts).WaitAsync(TimeSpan.FromSeconds(10));
        _bus.Dispose();
        _dir.Dispose();
    }

    private static string LastToolResult(FakeConversation conv) =>
        ((IReadOnlyList<ToolCallResult>)conv.Messages[^1])[0].Result.Content;

    private async Task StartAgentAsync(string name, Func<FakeConversation, LlmTurn> script, bool memory = false)
    {
        var directory = Path.Combine(_dir.Path, "agents", name);
        var agent = new AgentDefinition
        {
            Name = name,
            Directory = directory,
            Config = new AgentConfig { Name = name, Engine = "anthropic", MaxIterations = 5 },
            Soul = $"You are {name}.",
            Skills = new SkillCatalog([]),
            Memory = memory ? new MemoryStore(Path.Combine(directory, "memory")) : null
        };
        _hosts.Add(AgentHost.RunAsync(agent, _paths,
            (a, tools, events, options, _) => new ApiLoopEngine(a, new FakeLlmProvider(script), tools, events, options),
            _stop.Token));

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            try
            {
                await _bus.GetStatusAsync(name, CancellationToken.None);
                return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
        }
    }

    private Task<BusReply> AskAsync(string to, string text) =>
        _bus.SendAsync(new BusMessage(Guid.NewGuid().ToString("n")[..8], "cli:test", to, MessageKinds.Ask, text, TimeoutSeconds: 30), CancellationToken.None);

    [Fact]
    public async Task AgentsTalkOverTheBus()
    {
        await StartAgentAsync("beta", _ => FakeLlmProvider.Text("4"));
        await StartAgentAsync("alpha", conv => conv.Messages.Count == 1
            ? FakeLlmProvider.Call("send_message", """{"to":"beta","text":"what is 2+2?"}""")
            : FakeLlmProvider.Text("beta said: " + LastToolResult(conv)));

        var reply = await AskAsync("alpha", "ask beta");
        var agents = await _bus.ListAgentsAsync(CancellationToken.None);

        Assert.True(reply.Success, reply.Error);
        Assert.Equal("beta said: 4", reply.Output);
        Assert.Equal(["alpha", "beta"], agents.Select(a => a.Name));
        Assert.All(agents, a => Assert.Equal("idle", a.State));
        Assert.Equal(1, agents.Single(a => a.Name == "beta").CompletedTasks);
    }

    [Fact]
    public async Task AskCycleIsRejectedInsteadOfDeadlocking()
    {
        await StartAgentAsync("alpha", conv => conv.Messages.Count == 1
            ? FakeLlmProvider.Call("send_message", """{"to":"beta","text":"help me"}""")
            : FakeLlmProvider.Text("alpha got: " + LastToolResult(conv)));
        await StartAgentAsync("beta", conv => conv.Messages.Count == 1
            ? FakeLlmProvider.Call("send_message", """{"to":"alpha","text":"and you?"}""")
            : FakeLlmProvider.Text("beta could not ask: " + LastToolResult(conv)));

        var reply = await AskAsync("alpha", "start").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(reply.Success, reply.Error);
        Assert.Contains("ask cycle: alpha → beta → alpha", reply.Output);
    }

    [Fact]
    public async Task TellQueuesWithoutWaiting()
    {
        var gate = new TaskCompletionSource();
        await StartAgentAsync("gamma", _ =>
        {
            gate.Task.Wait(TimeSpan.FromSeconds(10));
            return FakeLlmProvider.Text("done");
        });

        var reply = await _bus.SendAsync(new BusMessage("t1", "cli:test", "gamma", MessageKinds.Tell, "background job"), CancellationToken.None);
        var busy = await _bus.GetStatusAsync("gamma", CancellationToken.None);
        gate.SetResult();

        Assert.True(reply.Queued);
        Assert.Equal("busy", busy.State);
        Assert.Equal("t1", busy.CurrentTaskId);
    }

    [Fact]
    public async Task SecondInstanceOfSameAgentIsRefused()
    {
        await StartAgentAsync("delta", _ => FakeLlmProvider.Text("x"));
        var agent = new AgentDefinition
        {
            Name = "delta",
            Directory = Path.Combine(_dir.Path, "agents", "delta2"),
            Config = new AgentConfig { Name = "delta" },
            Soul = "s",
            Skills = new SkillCatalog([])
        };

        var e = await Assert.ThrowsAsync<ConfigException>(() => AgentHost.RunAsync(agent, _paths,
            (a, tools, events, options, _) => new ApiLoopEngine(a, new FakeLlmProvider(_ => FakeLlmProvider.Text("y")), tools, events, options),
            CancellationToken.None));
        Assert.Contains("already running", e.Message);
    }

    [Fact]
    public async Task ExposesBridgeToolsAndRunsThem()
    {
        await StartAgentAsync("beta", _ => FakeLlmProvider.Text("pong"));
        await StartAgentAsync("epsilon", _ => FakeLlmProvider.Text("x"), memory: true);

        var tools = await _bus.ListToolsAsync("epsilon", CancellationToken.None);
        var listed = await _bus.InvokeToolAsync("epsilon",
            new ToolInvocation("list_agents", System.Text.Json.JsonDocument.Parse("{}").RootElement), CancellationToken.None);
        var saved = await _bus.InvokeToolAsync("epsilon",
            new ToolInvocation("memory_save", System.Text.Json.JsonDocument.Parse(
                """{"name":"beta-pongs","description":"Beta answers pong","content":"Always pong."}""").RootElement), CancellationToken.None);

        Assert.Equal(["send_message", "list_agents", "memory_save", "memory_read", "memory_search", "memory_delete"], tools.Select(t => t.Name));
        Assert.False(saved.IsError);
        Assert.True(File.Exists(Path.Combine(_dir.Path, "agents", "epsilon", "memory", "beta-pongs.md")));
        Assert.False(listed.IsError);
        Assert.Contains("- beta: anthropic", listed.Content);
        Assert.DoesNotContain("epsilon", listed.Content);
    }
}
