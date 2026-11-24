using System.Text;
using System.Text.RegularExpressions;
using Racka.Core.Skills;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Racka.Core.Memory;

public sealed record MemoryEntry(string Name, string Description, DateTimeOffset Updated, string Content);

public sealed record MemorySearchHit(MemoryEntry Entry, int Score, string Snippet);

/// <summary>
/// An agent's long-term memory: one Markdown file per memory with a name/description/updated frontmatter.
/// The files are plain text, so people can read and edit them; hand-written files without frontmatter work too.
/// </summary>
public sealed partial class MemoryStore
{
    public const int MaxContentChars = 20_000;
    public const int MaxDescriptionChars = 300;

    private static readonly IDeserializer FrontmatterDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly Lock _writeLock = new();

    public MemoryStore(string directory) => Directory = Path.GetFullPath(directory);

    public string Directory { get; }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex NamePattern();

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    public IReadOnlyList<MemoryEntry> List()
    {
        if (!System.IO.Directory.Exists(Directory))
            return [];
        var entries = new List<MemoryEntry>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.md"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!IsValidName(name))
                continue;
            if (TryLoad(file, name) is { } entry)
                entries.Add(entry);
        }
        entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return entries;
    }

    public MemoryEntry? Get(string name) => IsValidName(name) ? TryLoad(PathOf(name), name) : null;

    /// <summary>Creates or replaces a memory. Returns true when it is new.</summary>
    public bool Save(string name, string description, string content)
    {
        if (!IsValidName(name))
            throw new ArgumentException($"Invalid memory name '{name}': use lowercase letters, digits and '-' (max 64).");
        description = string.Join(' ', description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (description.Length == 0)
            throw new ArgumentException("A memory needs a one-line description.");
        if (description.Length > MaxDescriptionChars)
            throw new ArgumentException($"Description is {description.Length} characters; keep it under {MaxDescriptionChars}.");
        content = content.Replace("\r\n", "\n").Trim();
        if (content.Length > MaxContentChars)
            throw new ArgumentException($"Content is {content.Length} characters; keep a memory under {MaxContentChars}. Split it into several memories.");

        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("name: ").Append(name).Append('\n');
        sb.Append("description: ").Append(YamlQuote(description)).Append('\n');
        sb.Append("updated: ").Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")).Append('\n');
        sb.Append("---\n\n");
        sb.Append(content).Append('\n');

        lock (_writeLock)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = PathOf(name);
            var isNew = !File.Exists(path);
            var temp = path + ".tmp";
            File.WriteAllText(temp, sb.ToString());
            File.Move(temp, path, overwrite: true);
            return isNew;
        }
    }

    public bool Delete(string name)
    {
        if (!IsValidName(name))
            return false;
        lock (_writeLock)
        {
            var path = PathOf(name);
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
    }

    /// <summary>Ranks memories by how many query words they contain; matches in the name or description count more.</summary>
    public IReadOnlyList<MemorySearchHit> Search(string query, int limit = 10)
    {
        var words = query.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
        if (words.Length == 0)
            return [];
        var hits = new List<MemorySearchHit>();
        foreach (var entry in List())
        {
            var head = (entry.Name.Replace('-', ' ') + " " + entry.Description).ToLowerInvariant();
            var body = entry.Content.ToLowerInvariant();
            var score = 0;
            var firstMatch = -1;
            foreach (var word in words)
            {
                if (head.Contains(word, StringComparison.Ordinal))
                    score += 3;
                var at = body.IndexOf(word, StringComparison.Ordinal);
                if (at >= 0)
                {
                    score += 1;
                    if (firstMatch < 0 || at < firstMatch)
                        firstMatch = at;
                }
            }
            if (score > 0)
                hits.Add(new MemorySearchHit(entry, score, Snippet(entry.Content, firstMatch)));
        }
        return hits.OrderByDescending(h => h.Score).ThenByDescending(h => h.Entry.Updated).Take(limit).ToList();
    }

    /// <summary>One line per memory for the system prompt, cut at <paramref name="maxChars"/>.</summary>
    public string BuildIndex(int maxChars)
    {
        var entries = List();
        var sb = new StringBuilder();
        for (var i = 0; i < entries.Count; i++)
        {
            var line = $"- {entries[i].Name}: {entries[i].Description}\n";
            if (sb.Length + line.Length > maxChars)
            {
                sb.Append($"- …and {entries.Count - i} more (use memory_search)\n");
                break;
            }
            sb.Append(line);
        }
        return sb.ToString().TrimEnd();
    }

    private string PathOf(string name) => Path.Combine(Directory, name + ".md");

    private static MemoryEntry? TryLoad(string path, string name)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        var (frontmatter, body) = SkillCatalog.SplitFrontmatter(text);
        Frontmatter meta;
        try
        {
            meta = frontmatter == null ? new Frontmatter() : FrontmatterDeserializer.Deserialize<Frontmatter>(frontmatter) ?? new Frontmatter();
        }
        catch (YamlDotNet.Core.YamlException)
        {
            meta = new Frontmatter();
        }
        body = body.Trim();
        var description = meta.Description?.Trim();
        if (string.IsNullOrEmpty(description))
            description = body.Split('\n', 2)[0].TrimStart('#', ' ');
        var updated = DateTimeOffset.TryParse(meta.Updated, out var parsed) ? parsed : File.GetLastWriteTimeUtc(path);
        return new MemoryEntry(name, description, updated, body);
    }

    /// <summary>A YAML double-quoted scalar; accents and emoji stay readable for people editing the file.</summary>
    private static string YamlQuote(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            if (c is '"' or '\\')
                sb.Append('\\').Append(c);
            else if (char.IsControl(c))
                sb.Append($"\\x{(int)c:X2}");
            else
                sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    private static string Snippet(string content, int at)
    {
        const int radius = 80;
        if (at < 0)
            return content.Length <= 2 * radius ? content.Replace('\n', ' ') : content[..(2 * radius)].Replace('\n', ' ') + "…";
        var start = Math.Max(0, at - radius);
        var end = Math.Min(content.Length, at + radius);
        return (start > 0 ? "…" : "") + content[start..end].Replace('\n', ' ') + (end < content.Length ? "…" : "");
    }

    private sealed class Frontmatter
    {
        public string? Description { get; set; }
        public string? Updated { get; set; }
    }
}
