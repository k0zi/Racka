using Racka.Core.Engine;
using Racka.Core.Engine.ApiLoop;
using Racka.Core.Llm;
using Racka.Core.Tools;

namespace Racka.Tests;

public class ApiLoopEngineTests
{
    private static readonly PromptOptions Options = new(IncludeSkillIndex: true, CanMessageAgents: false);

    private static ApiLoopEngine Engine(FakeLlmProvider provider, int maxIterations = 5, params IRackaTool[] tools) =>
        new(TestAgents.Create(maxIterations: maxIterations), provider, tools, NullAgentEvents.Instance, Options);

    [Fact]
    public async Task ReturnsTextWhenNoToolCalls()
    {
        var provider = new FakeLlmProvider(_ => FakeLlmProvider.Text("  Baa!  "));
        var result = await Engine(provider).RunAsync(AgentTask.Create("hello", TaskSources.Cli), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Baa!", result.Output);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task ExecutesToolCallsThenFinishes()
    {
        var echo = new EchoTool();
        ToolCallResult? seen = null;
        var provider = new FakeLlmProvider(conv =>
        {
            if (conv.Messages.Count == 1)
                return FakeLlmProvider.Call("echo", """{"text":"hi"}""");
            seen = ((IReadOnlyList<ToolCallResult>)conv.Messages[^1])[0];
            return FakeLlmProvider.Text("done");
        });

        var result = await Engine(provider, tools: echo).RunAsync(AgentTask.Create("go", TaskSources.Cli), CancellationToken.None);

        Assert.Equal("done", result.Output);
        Assert.Equal(1, echo.Invocations);
        Assert.Equal("echo:hi", seen!.Result.Content);
        Assert.False(seen.Result.IsError);
    }

    [Fact]
    public async Task ToolFailuresBecomeErrorResults()
    {
        var results = new List<ToolCallResult>();
        var provider = new FakeLlmProvider(conv =>
        {
            if (conv.Messages.Count == 1)
                return new LlmTurn("", [
                    FakeLlmProvider.Call("boom", "{}").ToolCalls[0],
                    FakeLlmProvider.Call("missing", "{}").ToolCalls[0],
                    FakeLlmProvider.Call("echo", "{}").ToolCalls[0]
                ], "tool_use");
            results.AddRange((IReadOnlyList<ToolCallResult>)conv.Messages[^1]);
            return FakeLlmProvider.Text("handled");
        });

        var result = await Engine(provider, 5, new ThrowingTool(), new EchoTool()).RunAsync(AgentTask.Create("go", TaskSources.Cli), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.Result.IsError));
        Assert.Contains("kaboom", results[0].Result.Content);
        Assert.Contains("Unknown tool", results[1].Result.Content);
        Assert.Contains("text", results[2].Result.Content);
    }

    [Fact]
    public async Task StopsAtIterationLimitAndAnswersPendingCalls()
    {
        FakeConversation? conversation = null;
        var provider = new FakeLlmProvider(conv =>
        {
            conversation = conv;
            return FakeLlmProvider.Call("echo", """{"text":"again"}""", "still working");
        });

        var result = await Engine(provider, 3, new EchoTool()).RunAsync(AgentTask.Create("loop", TaskSources.Cli), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(3, provider.Calls);
        Assert.Contains("iteration limit", result.Output);
        var last = Assert.IsAssignableFrom<IReadOnlyList<ToolCallResult>>(conversation!.Messages[^1]);
        Assert.True(last[0].Result.IsError);
    }

    [Fact]
    public async Task ConversationContinuesForSameKey()
    {
        var sizes = new List<int>();
        var provider = new FakeLlmProvider(conv =>
        {
            sizes.Add(conv.Messages.Count);
            return FakeLlmProvider.Text("ok");
        });
        var engine = Engine(provider);

        await engine.RunAsync(AgentTask.Create("one", TaskSources.Irc, "k0zi", "irc:#racka"), CancellationToken.None);
        await engine.RunAsync(AgentTask.Create("two", TaskSources.Irc, "k0zi", "irc:#racka"), CancellationToken.None);
        await engine.RunAsync(AgentTask.Create("three", TaskSources.Cli), CancellationToken.None);

        Assert.Equal([1, 3, 1], sizes);
    }

    [Fact]
    public async Task ProviderErrorFailsTaskAndResetsConversation()
    {
        var calls = 0;
        var provider = new FakeLlmProvider(_ => ++calls == 1 ? throw new HttpRequestException("down") : FakeLlmProvider.Text("back"));
        var engine = Engine(provider);

        var failed = await engine.RunAsync(AgentTask.Create("a", TaskSources.Cli), CancellationToken.None);
        var ok = await engine.RunAsync(AgentTask.Create("b", TaskSources.Cli), CancellationToken.None);

        Assert.False(failed.Success);
        Assert.Contains("down", failed.Error);
        Assert.Equal("back", ok.Output);
    }

    [Fact]
    public async Task CancellationIsReported()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var provider = new FakeLlmProvider(_ => FakeLlmProvider.Text("never"));

        var result = await Engine(provider).RunAsync(AgentTask.Create("a", TaskSources.Cli), cts.Token);

        Assert.False(result.Success);
        Assert.Equal("cancelled", result.Error);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public void SystemPromptContainsSoulAndSkillIndex()
    {
        using var dir = new TempDir();
        dir.Write("s/SKILL.md", "---\nname: shearing\ndescription: How to shear\n---\nx");
        var skills = new Racka.Core.Skills.SkillCatalog([Racka.Core.Skills.SkillCatalog.LoadSkill(Path.Combine(dir.Path, "s"))]);
        var agent = TestAgents.Create(skills: skills);

        var prompt = PromptBuilder.BuildSystemPrompt(agent, Options);

        Assert.StartsWith(agent.Soul, prompt);
        Assert.Contains("- shearing: How to shear", prompt);
        Assert.DoesNotContain("send_message", prompt);
    }
}
