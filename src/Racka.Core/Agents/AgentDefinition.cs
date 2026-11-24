using Racka.Core.Config;
using Racka.Core.Memory;
using Racka.Core.Skills;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Racka.Core.Agents;

/// <summary>A fully loaded agent: config, soul and skills, with paths resolved against the agent directory.</summary>
public sealed class AgentDefinition
{
    public const string ConfigFileName = "agent.yaml";
    public const string SoulFileName = "soul.md";

    public required string Name { get; init; }
    public required string Directory { get; init; }
    public required AgentConfig Config { get; init; }
    public required string Soul { get; init; }
    public required SkillCatalog Skills { get; init; }
    /// <summary>Long-term memory; null when <c>memory.enabled</c> is false.</summary>
    public MemoryStore? Memory { get; init; }

    public string WorkDirectory => Path.GetFullPath(Path.Combine(Directory, Config.Workdir));
    public string StateDirectory => Path.Combine(Directory, "state");
    public string SkillsDirectory => Path.Combine(Directory, "skills");

    public static AgentDefinition Load(string agentDirectory, Func<string, string?>? envLookup = null)
    {
        var dir = Path.GetFullPath(agentDirectory);
        var configPath = Path.Combine(dir, ConfigFileName);
        if (!File.Exists(configPath))
            throw new ConfigException($"Missing {ConfigFileName} in '{dir}'.");
        var soulPath = Path.Combine(dir, SoulFileName);
        if (!File.Exists(soulPath))
            throw new ConfigException($"Missing {SoulFileName} in '{dir}'. Every agent needs a soul.");

        var config = ParseConfig(File.ReadAllText(configPath), envLookup);
        if (string.IsNullOrWhiteSpace(config.Name))
            config.Name = Path.GetFileName(dir);
        Validate(config);

        var sharedSkillsDir = Path.GetFullPath(Path.Combine(dir, config.SharedSkillsDir));
        var skills = SkillCatalog.Load(Path.Combine(dir, "skills"), sharedSkillsDir, config.Skills);

        return new AgentDefinition
        {
            Name = config.Name,
            Directory = dir,
            Config = config,
            Soul = File.ReadAllText(soulPath).Trim(),
            Skills = skills,
            Memory = config.Memory.Enabled ? new MemoryStore(Path.Combine(dir, config.Memory.Dir)) : null
        };
    }

    public static AgentConfig ParseConfig(string yaml, Func<string, string?>? envLookup = null)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithNodeDeserializer(new EnvExpandingStringDeserializer(envLookup), s => s.OnTop())
            .Build();
        try
        {
            return deserializer.Deserialize<AgentConfig>(yaml) ?? new AgentConfig();
        }
        catch (YamlDotNet.Core.YamlException e) when (e.InnerException is ConfigException ce)
        {
            throw ce;
        }
        catch (YamlDotNet.Core.YamlException e)
        {
            throw new ConfigException($"Invalid {ConfigFileName}: {e.Message}");
        }
    }

    private static void Validate(AgentConfig config)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(config.Name, "^[a-z0-9][a-z0-9_-]{0,31}$"))
            throw new ConfigException($"Agent name '{config.Name}' must be lowercase letters, digits, '-' or '_' (max 32).");
        _ = config.EngineKind;
        if (config.MaxIterations < 1)
            throw new ConfigException("maxIterations must be at least 1.");
        if (config.Memory.MaxIndexChars < 0)
            throw new ConfigException("memory.maxIndexChars must not be negative.");
        foreach (var (name, server) in config.McpServers)
        {
            if (server.IsStdio == !string.IsNullOrEmpty(server.Url))
                throw new ConfigException($"MCP server '{name}' needs exactly one of 'command' or 'url'.");
        }
        if (config.Irc is { } irc)
        {
            if (string.IsNullOrWhiteSpace(irc.Server))
                throw new ConfigException("irc.server is required when irc is configured.");
            if (string.IsNullOrWhiteSpace(irc.Nick))
                irc.Nick = config.Name;
        }
    }
}
