namespace Racka.Core.Skills;

/// <summary>A Claude Code compatible skill: a directory containing SKILL.md with YAML frontmatter.</summary>
public sealed record Skill(string Name, string Description, string Directory, string Body)
{
    public string SkillFile => Path.Combine(Directory, "SKILL.md");

    public IReadOnlyList<string> ListFiles() =>
        System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Directory, f))
            .Where(f => f != "SKILL.md")
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Reads a file inside the skill directory; rejects paths that escape it.</summary>
    public string ReadFile(string relativePath)
    {
        var root = Path.GetFullPath(Directory) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(Directory, relativePath));
        if (!full.StartsWith(root, StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"Path '{relativePath}' is outside of skill '{Name}'.");
        return File.ReadAllText(full);
    }
}
