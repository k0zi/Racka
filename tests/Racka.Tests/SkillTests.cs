using System.Text.Json;
using Racka.Core.Engine;
using Racka.Core.Skills;
using Racka.Core.Tools;

namespace Racka.Tests;

public class SkillTests
{
    [Fact]
    public void ParsesFrontmatterAndBody()
    {
        using var dir = new TempDir();
        dir.Write("review/SKILL.md", "---\nname: code-review\ndescription: Review code changes\nallowed-tools: Read\n---\n\n# Steps\n1. Read\n");
        var skill = SkillCatalog.LoadSkill(Path.Combine(dir.Path, "review"));

        Assert.Equal("code-review", skill.Name);
        Assert.Equal("Review code changes", skill.Description);
        Assert.Equal("# Steps\n1. Read", skill.Body);
    }

    [Fact]
    public void NameFallsBackToDirectory()
    {
        using var dir = new TempDir();
        dir.Write("plain/SKILL.md", "Just instructions.");
        var skill = SkillCatalog.LoadSkill(Path.Combine(dir.Path, "plain"));

        Assert.Equal("plain", skill.Name);
        Assert.Equal("Just instructions.", skill.Body);
    }

    [Fact]
    public void ReadFileRejectsTraversal()
    {
        using var dir = new TempDir();
        dir.Write("s/SKILL.md", "x");
        dir.Write("s/ref/notes.md", "notes");
        dir.Write("secret.txt", "secret");
        var skill = SkillCatalog.LoadSkill(Path.Combine(dir.Path, "s"));

        Assert.Equal("notes", skill.ReadFile("ref/notes.md"));
        Assert.Throws<UnauthorizedAccessException>(() => skill.ReadFile("../secret.txt"));
        Assert.Equal(["ref/notes.md"], skill.ListFiles());
    }

    [Fact]
    public void MissingSharedSkillFails()
    {
        using var dir = new TempDir();
        Assert.Throws<Racka.Core.Config.ConfigException>(() => SkillCatalog.Load(null, dir.Path, ["nope"]));
    }

    [Fact]
    public async Task LoadSkillToolReturnsBodyAndFiles()
    {
        using var dir = new TempDir();
        dir.Write("s/SKILL.md", "---\ndescription: d\n---\nBody text");
        dir.Write("s/template.txt", "t");
        var catalog = new SkillCatalog([SkillCatalog.LoadSkill(Path.Combine(dir.Path, "s"))]);
        var tool = new LoadSkillTool(catalog);
        var ctx = new ToolContext(TestAgents.Create(), AgentTask.Create("x", TaskSources.Cli), NullAgentEvents.Instance);

        var ok = await tool.InvokeAsync(JsonDocument.Parse("""{"name":"s"}""").RootElement, ctx, CancellationToken.None);
        var missing = await tool.InvokeAsync(JsonDocument.Parse("""{"name":"x"}""").RootElement, ctx, CancellationToken.None);

        Assert.False(ok.IsError);
        Assert.Contains("Body text", ok.Content);
        Assert.Contains("template.txt", ok.Content);
        Assert.True(missing.IsError);
    }
}
