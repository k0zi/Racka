using System.Text.Json;
using Racka.Core.Agents;
using Racka.Core.Config;
using Racka.Core.Engine;
using Racka.Core.Engine.ApiLoop;
using Racka.Core.Memory;
using Racka.Core.Skills;
using Racka.Core.Tools;
using Racka.Providers.ClaudeCode;

namespace Racka.Tests;

public class MemoryTests
{
    private static readonly PromptOptions Options = new(IncludeSkillIndex: true, CanMessageAgents: false);

    private static AgentDefinition AgentWithMemory(TempDir dir, int maxIndexChars = 8000)
    {
        var config = new AgentConfig { Name = "dolly", MaxIterations = 5 };
        config.Memory.MaxIndexChars = maxIndexChars;
        return new AgentDefinition
        {
            Name = "dolly",
            Directory = dir.Path,
            Config = config,
            Soul = "You are Dolly.",
            Skills = new SkillCatalog([]),
            Memory = new MemoryStore(Path.Combine(dir.Path, "memory"))
        };
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ToolContext Context(AgentDefinition agent) =>
        new(agent, AgentTask.Create("t", TaskSources.Cli), NullAgentEvents.Instance);

    [Fact]
    public void SavesReadsUpdatesAndDeletes()
    {
        using var dir = new TempDir();
        var store = new MemoryStore(dir.Path);

        Assert.True(store.Save("owner-language", "Owner: \"prefers\" Hungarian\n  answers", "Answer in Hungarian.\r\nKeep code in English."));
        var entry = store.Get("owner-language")!;
        Assert.Equal("Owner: \"prefers\" Hungarian answers", entry.Description);
        Assert.Equal("Answer in Hungarian.\nKeep code in English.", entry.Content);
        Assert.True(DateTimeOffset.UtcNow - entry.Updated < TimeSpan.FromMinutes(1));

        Assert.False(store.Save("owner-language", "Árvíztűrő \\ 🐑 gazda", "Answer in Hungarian."));
        Assert.Equal("Árvíztűrő \\ 🐑 gazda", store.Get("owner-language")!.Description);
        Assert.Contains("description: \"Árvíztűrő \\\\ 🐑 gazda\"", File.ReadAllText(Path.Combine(dir.Path, "owner-language.md")));
        Assert.Equal("Answer in Hungarian.", store.Get("owner-language")!.Content);
        Assert.Single(store.List());

        Assert.True(store.Delete("owner-language"));
        Assert.False(store.Delete("owner-language"));
        Assert.Null(store.Get("owner-language"));
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Theory]
    [InlineData("../escape", "d", "c")]
    [InlineData("Upper", "d", "c")]
    [InlineData("ok", "  ", "c")]
    public void RejectsInvalidInput(string name, string description, string content)
    {
        using var dir = new TempDir();
        Assert.Throws<ArgumentException>(() => new MemoryStore(dir.Path).Save(name, description, content));
    }

    [Fact]
    public void RejectsOversizedContent()
    {
        using var dir = new TempDir();
        var e = Assert.Throws<ArgumentException>(() =>
            new MemoryStore(dir.Path).Save("big", "too big", new string('x', MemoryStore.MaxContentChars + 1)));
        Assert.Contains("Split it", e.Message);
    }

    [Fact]
    public void ReadsHandWrittenFilesAndSkipsOthers()
    {
        using var dir = new TempDir();
        dir.Write("deploy-steps.md", "# Deploy runs from the ops box\n\nssh ops, then ./deploy.sh");
        dir.Write("Not A Memory.md", "ignored");
        dir.Write("notes.txt", "ignored");
        var store = new MemoryStore(dir.Path);

        var entry = Assert.Single(store.List());
        Assert.Equal("deploy-steps", entry.Name);
        Assert.Equal("Deploy runs from the ops box", entry.Description);
        Assert.Null(store.Get("../notes"));
    }

    [Fact]
    public void SearchRanksNameAndDescriptionMatchesHigher()
    {
        using var dir = new TempDir();
        var store = new MemoryStore(dir.Path);
        store.Save("build-command", "How to build Racka", "Run dotnet build in src.");
        store.Save("irc-etiquette", "Keep IRC replies short", "Nobody reads long build logs on IRC.");
        store.Save("unrelated", "Lunch is at noon", "Sandwiches.");

        var hits = store.Search("BUILD");

        Assert.Equal(["build-command", "irc-etiquette"], hits.Select(h => h.Entry.Name));
        Assert.Contains("build logs", hits[1].Snippet);
        Assert.Empty(store.Search("   "));
    }

    [Fact]
    public void IndexIsSortedAndCut()
    {
        using var dir = new TempDir();
        var store = new MemoryStore(dir.Path);
        Assert.Equal("", store.BuildIndex(1000));
        foreach (var name in new[] { "c-third", "a-first", "b-second" })
            store.Save(name, "about " + name, "x");

        Assert.Equal("- a-first: about a-first\n- b-second: about b-second\n- c-third: about c-third", store.BuildIndex(1000));
        Assert.Equal("- a-first: about a-first\n- …and 2 more (use memory_search)", store.BuildIndex(40));
    }

    [Fact]
    public async Task ToolsSaveSearchReadAndDelete()
    {
        using var dir = new TempDir();
        var agent = AgentWithMemory(dir);
        var tools = MemoryTools.Create(agent.Memory!).ToDictionary(t => t.Name);
        var ctx = Context(agent);

        var saved = await tools["memory_save"].InvokeAsync(
            Args("""{"name":"shaun-delegates","description":"Shaun sends code tasks","content":"Shaun forwards coding work to me."}"""), ctx, default);
        var listed = await tools["memory_search"].InvokeAsync(Args("{}"), ctx, default);
        var found = await tools["memory_search"].InvokeAsync(Args("""{"query":"coding"}"""), ctx, default);
        var read = await tools["memory_read"].InvokeAsync(Args("""{"name":"shaun-delegates"}"""), ctx, default);
        var bad = await tools["memory_save"].InvokeAsync(Args("""{"name":"Bad Name","description":"d","content":"c"}"""), ctx, default);
        var deleted = await tools["memory_delete"].InvokeAsync(Args("""{"name":"shaun-delegates"}"""), ctx, default);
        var missing = await tools["memory_read"].InvokeAsync(Args("""{"name":"shaun-delegates"}"""), ctx, default);

        Assert.Equal("Saved new memory 'shaun-delegates'.", saved.Content);
        Assert.Equal("- shaun-delegates: Shaun sends code tasks", listed.Content);
        Assert.Contains("forwards coding work", found.Content);
        Assert.Contains("Shaun forwards coding work to me.", read.Content);
        Assert.True(bad.IsError);
        Assert.False(deleted.IsError);
        Assert.True(missing.IsError);
    }

    [Fact]
    public void SystemPromptListsMemories()
    {
        using var dir = new TempDir();
        var agent = AgentWithMemory(dir);

        Assert.Contains("You have no memories yet.", PromptBuilder.BuildSystemPrompt(agent, Options));
        agent.Memory!.Save("owner-language", "Owner prefers Hungarian", "x");
        var prompt = PromptBuilder.BuildSystemPrompt(agent, Options);

        Assert.Contains("# Memory", prompt);
        Assert.Contains("- owner-language: Owner prefers Hungarian", prompt);
        Assert.DoesNotContain("# Memory", PromptBuilder.BuildSystemPrompt(TestAgents.Create(), Options));
    }

    [Fact]
    public async Task NewConversationSeesMemorySavedInAnEarlierOne()
    {
        using var dir = new TempDir();
        var agent = AgentWithMemory(dir);
        var prompts = new List<string>();
        var provider = new FakeLlmProvider(conv =>
        {
            if (conv.Messages.Count == 1)
                prompts.Add(conv.SystemPrompt);
            if (prompts.Count == 1 && conv.Messages.Count == 1)
                return FakeLlmProvider.Call("memory_save", """{"name":"likes-tea","description":"Owner likes tea","content":"Green tea."}""");
            return FakeLlmProvider.Text("ok");
        });
        var engine = new ApiLoopEngine(agent, provider, MemoryTools.Create(agent.Memory!), NullAgentEvents.Instance, Options);

        Assert.True((await engine.RunAsync(AgentTask.Create("remember tea", TaskSources.Cli, conversationKey: "a"), default)).Success);
        Assert.True((await engine.RunAsync(AgentTask.Create("hi", TaskSources.Cli, conversationKey: "b"), default)).Success);

        Assert.DoesNotContain("likes-tea", prompts[0]);
        Assert.Contains("- likes-tea: Owner likes tea", prompts[1]);
    }

    [Fact]
    public void ClaudeCodeSystemPromptIncludesCurrentMemories()
    {
        using var dir = new TempDir();
        var agent = AgentWithMemory(dir);
        var engine = new ClaudeCodeEngine(agent, NullAgentEvents.Instance, Options with { IncludeSkillIndex = false }, null);

        agent.Memory!.Save("repo-layout", "Sources live in src/", "x");
        var args = engine.BuildArguments(null).ToList();

        Assert.Contains("- repo-layout: Sources live in src/", args[args.IndexOf("--append-system-prompt") + 1]);
    }

    [Fact]
    public void LoadEnablesMemoryByDefault()
    {
        using var dir = new TempDir();
        dir.Write("on/agent.yaml", "engine: anthropic");
        dir.Write("on/soul.md", "x");
        dir.Write("off/agent.yaml", "engine: anthropic\nmemory: { enabled: false }");
        dir.Write("off/soul.md", "x");

        var on = AgentDefinition.Load(Path.Combine(dir.Path, "on"));
        var off = AgentDefinition.Load(Path.Combine(dir.Path, "off"));

        Assert.Equal(Path.Combine(dir.Path, "on", "memory"), on.Memory!.Directory);
        Assert.Null(off.Memory);
    }
}
