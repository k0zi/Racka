using Racka.Core.Config;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Racka.Core.Skills;

public sealed class SkillCatalog
{
    private static readonly IDeserializer FrontmatterDeserializer = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly Dictionary<string, Skill> _skills;

    public SkillCatalog(IEnumerable<Skill> skills)
    {
        _skills = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in skills)
            _skills[skill.Name] = skill;
    }

    public IReadOnlyCollection<Skill> All => _skills.Values;

    public Skill? Find(string name) => _skills.GetValueOrDefault(name);

    /// <summary>
    /// Loads every skill from the agent's own directory plus the requested shared skills.
    /// Agent-local skills win over shared skills with the same name.
    /// </summary>
    public static SkillCatalog Load(string? agentSkillsDir, string? sharedSkillsDir, IEnumerable<string> sharedSkillNames)
    {
        var skills = new List<Skill>();
        foreach (var name in sharedSkillNames)
        {
            var dir = sharedSkillsDir == null ? null : Path.Combine(sharedSkillsDir, name);
            if (dir == null || !File.Exists(Path.Combine(dir, "SKILL.md")))
                throw new ConfigException($"Shared skill '{name}' not found in '{sharedSkillsDir}'.");
            skills.Add(LoadSkill(dir));
        }
        if (agentSkillsDir != null && Directory.Exists(agentSkillsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(agentSkillsDir).Order(StringComparer.Ordinal))
            {
                if (File.Exists(Path.Combine(dir, "SKILL.md")))
                    skills.Add(LoadSkill(dir));
            }
        }
        return new SkillCatalog(skills);
    }

    public static Skill LoadSkill(string directory)
    {
        var text = File.ReadAllText(Path.Combine(directory, "SKILL.md"));
        var (frontmatter, body) = SplitFrontmatter(text);
        var meta = frontmatter == null
            ? new SkillFrontmatter()
            : FrontmatterDeserializer.Deserialize<SkillFrontmatter>(frontmatter) ?? new SkillFrontmatter();
        var name = string.IsNullOrWhiteSpace(meta.Name) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)) : meta.Name.Trim();
        return new Skill(name, meta.Description?.Trim() ?? "", Path.GetFullPath(directory), body.Trim());
    }

    internal static (string? Frontmatter, string Body) SplitFrontmatter(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n"))
            return (null, normalized);
        var end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
            return (null, normalized);
        var frontmatter = normalized[4..end];
        var bodyStart = normalized.IndexOf('\n', end + 4);
        var body = bodyStart < 0 ? "" : normalized[(bodyStart + 1)..];
        return (frontmatter, body);
    }

    private sealed class SkillFrontmatter
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
    }
}
