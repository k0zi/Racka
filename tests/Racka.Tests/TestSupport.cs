using System.Text.Json;
using Racka.Core.Agents;
using Racka.Core.Config;
using Racka.Core.Engine;
using Racka.Core.Llm;
using Racka.Core.Skills;
using Racka.Core.Tools;

namespace Racka.Tests;

internal sealed class FakeConversation : IConversation
{
    public string SystemPrompt { get; init; } = "";
    public List<object> Messages { get; } = new();

    public void AddUserText(string text) => Messages.Add(text);
    public void AddToolResults(IReadOnlyList<ToolCallResult> results) => Messages.Add(results);
}

/// <summary>Replies with scripted turns; the script sees the conversation so far.</summary>
internal sealed class FakeLlmProvider(Func<FakeConversation, LlmTurn> script) : ILlmProvider
{
    public int Calls { get; private set; }

    public IConversation CreateConversation(string systemPrompt) => new FakeConversation { SystemPrompt = systemPrompt };

    public Task<LlmTurn> NextTurnAsync(IConversation conversation, IReadOnlyList<IRackaTool> tools, CancellationToken ct)
    {
        Calls++;
        var conv = (FakeConversation)conversation;
        var turn = script(conv);
        conv.Messages.Add(turn);
        return Task.FromResult(turn);
    }

    public static LlmTurn Text(string text) => new(text, [], "end_turn");

    public static LlmTurn Call(string name, string argsJson, string text = "") =>
        new(text, [new ToolCall(Guid.NewGuid().ToString("n")[..8], name, JsonDocument.Parse(argsJson).RootElement.Clone())], "tool_use");
}

internal sealed class EchoTool() : RackaTool("echo", "Echo the text back.",
    """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""")
{
    public int Invocations;

    public override Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        Interlocked.Increment(ref Invocations);
        return Task.FromResult(ToolResult.Ok("echo:" + RequireString(arguments, "text")));
    }
}

internal sealed class ThrowingTool() : RackaTool("boom", "Always fails.", """{"type":"object","properties":{}}""")
{
    public override Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext context, CancellationToken ct) =>
        throw new InvalidOperationException("kaboom");
}

/// <summary>A temporary directory deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "racka-tests-" + Guid.NewGuid().ToString("n")[..8]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string Write(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { /* best effort */ }
    }
}

internal static class TestAgents
{
    public static AgentDefinition Create(string name = "dolly", int maxIterations = 5, SkillCatalog? skills = null) => new()
    {
        Name = name,
        Directory = System.IO.Path.GetTempPath(),
        Config = new AgentConfig { Name = name, MaxIterations = maxIterations },
        Soul = $"You are {name}, a cheerful racka.",
        Skills = skills ?? new SkillCatalog([])
    };
}
