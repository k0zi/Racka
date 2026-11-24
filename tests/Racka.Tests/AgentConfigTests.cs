using Racka.Core.Agents;
using Racka.Core.Config;

namespace Racka.Tests;

public class AgentConfigTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] vars) =>
        name => vars.FirstOrDefault(v => v.Key == name).Value;

    [Fact]
    public void ParsesFullConfig()
    {
        var yaml = """
            name: dolly
            engine: claude-code
            model: claude-opus-5-5
            effort: high
            openai: { baseUrl: http://localhost:11434/v1 }
            claudeCode: { permissionMode: acceptEdits, allowedTools: [Read, "Bash(git:*)"], maxTurns: 30 }
            mcpServers:
              github: { command: github-mcp, args: [stdio], env: { GITHUB_TOKEN: "${GH}" } }
              docs: { url: https://example.com/mcp }
            skills: [code-review]
            irc:
              server: irc.example.net
              channels: ["#racka"]
              operators: ["k0zi!*@*"]
            """;
        var config = AgentDefinition.ParseConfig(yaml, Env(("GH", "secret")));

        Assert.Equal(EngineKind.ClaudeCode, config.EngineKind);
        Assert.Equal("claude-opus-5-5", config.Model);
        Assert.Equal("http://localhost:11434/v1", config.OpenAI.BaseUrl);
        Assert.Equal(["Read", "Bash(git:*)"], config.ClaudeCode.AllowedTools);
        Assert.Equal(30, config.ClaudeCode.MaxTurns);
        Assert.Equal("secret", config.McpServers["github"].Env["GITHUB_TOKEN"]);
        Assert.True(config.McpServers["github"].IsStdio);
        Assert.False(config.McpServers["docs"].IsStdio);
        Assert.Equal(["code-review"], config.Skills);
        Assert.Equal(6697, config.Irc!.Port);
        Assert.Equal(["#racka"], config.Irc.Channels);
    }

    [Fact]
    public void EnvDefaultIsUsedWhenVariableMissing()
    {
        var config = AgentDefinition.ParseConfig("model: ${MODEL:-claude-sonnet-5-5}", Env());
        Assert.Equal("claude-sonnet-5-5", config.Model);
    }

    [Fact]
    public void MissingEnvVariableFails()
    {
        var e = Assert.Throws<ConfigException>(() => AgentDefinition.ParseConfig("model: ${NOPE}", Env()));
        Assert.Contains("NOPE", e.Message);
    }

    [Fact]
    public void UnknownEngineFails()
    {
        var config = AgentDefinition.ParseConfig("engine: gemini");
        Assert.Throws<ConfigException>(() => config.EngineKind);
    }

    [Fact]
    public void LoadRequiresSoul()
    {
        using var dir = new TempDir();
        dir.Write("agents/dolly/agent.yaml", "engine: anthropic");
        var e = Assert.Throws<ConfigException>(() => AgentDefinition.Load(Path.Combine(dir.Path, "agents/dolly")));
        Assert.Contains("soul.md", e.Message);
    }

    [Fact]
    public void LoadResolvesNameSoulAndSkills()
    {
        using var dir = new TempDir();
        dir.Write("agents/dolly/agent.yaml", "engine: anthropic\nskills: [shared-one]");
        dir.Write("agents/dolly/soul.md", "  Kind and curious.  \n");
        dir.Write("agents/dolly/skills/own/SKILL.md", "---\nname: own\ndescription: Own skill\n---\nDo own things.");
        dir.Write("skills/shared-one/SKILL.md", "---\ndescription: Shared skill\n---\nShared body.");

        var agent = AgentDefinition.Load(Path.Combine(dir.Path, "agents/dolly"));

        Assert.Equal("dolly", agent.Name);
        Assert.Equal("Kind and curious.", agent.Soul);
        Assert.Equal(["own", "shared-one"], agent.Skills.All.Select(s => s.Name).Order());
        Assert.Equal(Path.Combine(dir.Path, "agents/dolly/workspace"), agent.WorkDirectory);
    }

    [Fact]
    public void InvalidNameFails()
    {
        using var dir = new TempDir();
        dir.Write("a/agent.yaml", "name: Bad Name");
        dir.Write("a/soul.md", "x");
        Assert.Throws<ConfigException>(() => AgentDefinition.Load(Path.Combine(dir.Path, "a")));
    }

    [Fact]
    public void McpServerNeedsCommandOrUrl()
    {
        using var dir = new TempDir();
        dir.Write("a/agent.yaml", "name: a\nmcpServers:\n  broken: { args: [x] }");
        dir.Write("a/soul.md", "x");
        Assert.Throws<ConfigException>(() => AgentDefinition.Load(Path.Combine(dir.Path, "a")));
    }
}
