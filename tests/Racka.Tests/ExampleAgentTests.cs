using Racka.Core.Agents;
using Racka.Core.Config;
using Racka.Core.Engine;

namespace Racka.Tests;

/// <summary>Keeps the example agents in the repository loadable.</summary>
public class ExampleAgentTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "agents")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("agents/ not found above the test directory");
    }

    [Theory]
    [InlineData("agents/dolly", EngineKind.ClaudeCode, "code-review")]
    [InlineData("agents/shaun", EngineKind.Anthropic, "status-report")]
    [InlineData("agents/merino", EngineKind.Codex, "status-report")]
    [InlineData("agents/lamb", EngineKind.OpenAI, "changelog")]
    [InlineData("samples/dotnet/CustomTool/agent", EngineKind.OpenAI, null)]
    public void ExampleAgentLoads(string path, EngineKind engine, string? skill)
    {
        var agent = AgentDefinition.Load(Path.Combine(RepoRoot(), path), _ => null);

        Assert.Equal(engine, agent.Config.EngineKind);
        Assert.NotNull(agent.Memory);
        var prompt = PromptBuilder.BuildSystemPrompt(agent, new PromptOptions(true, true));
        if (skill != null)
        {
            Assert.NotNull(agent.Skills.Find(skill));
            Assert.Contains(skill, prompt);
        }
    }

    [Fact]
    public void HerdSharesOneWorkspace()
    {
        var root = RepoRoot();
        var dolly = AgentDefinition.Load(Path.Combine(root, "agents", "dolly"));
        var merino = AgentDefinition.Load(Path.Combine(root, "agents", "merino"));

        Assert.Equal(Path.Combine(root, "workspace"), dolly.WorkDirectory);
        Assert.Equal(dolly.WorkDirectory, merino.WorkDirectory);
    }
}
