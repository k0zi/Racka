using Racka.Core.Config;
using Racka.Core.Memory;
using Racka.Core.Skills;
using YamlDotNet.RepresentationModel;

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

    /// <param name="layers">User settings below agent.yaml and command line overrides above it (chat sections only).</param>
    public static AgentDefinition Load(string agentDirectory, Func<string, string?>? envLookup = null, ConfigLayers? layers = null)
    {
        var dir = Path.GetFullPath(agentDirectory);
        var configPath = Path.Combine(dir, ConfigFileName);
        if (!File.Exists(configPath))
            throw new ConfigException($"Missing {ConfigFileName} in '{dir}'.");
        var soulPath = Path.Combine(dir, SoulFileName);
        if (!File.Exists(soulPath))
            throw new ConfigException($"Missing {SoulFileName} in '{dir}'. Every agent needs a soul.");

        var yaml = File.ReadAllText(configPath);
        if (layers != null)
            yaml = ApplyLayers(yaml, configPath, layers);
        var config = ParseConfig(yaml, envLookup);
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
        var deserializer = RackaYaml.Deserializer(envLookup);
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
        foreach (var chat in config.ChatConnections)
        {
            chat.ApplyAgentName(config.Name);
            if (chat.MaxReplyLines < 1)
                throw new ConfigException($"{chat.Platform}.maxReplyLines must be at least 1.");
        }
        if (config.Irc is { Enabled: true } irc && string.IsNullOrWhiteSpace(irc.Server))
            throw new ConfigException("irc.server is required when irc is configured (in agent.yaml or the user settings).");
        if (config.Teams is { Enabled: true } teams)
        {
            if (string.IsNullOrWhiteSpace(teams.AppId) || string.IsNullOrWhiteSpace(teams.TenantId))
                throw new ConfigException("teams.appId and teams.tenantId are required when teams is configured.");
            if (teams.Port is < 1 or > 65535)
                throw new ConfigException("teams.port must be between 1 and 65535.");
        }
    }

    /// <summary>Merges the chat sections: user settings, then agent.yaml, then overrides, field by field.</summary>
    internal static string ApplyLayers(string agentYaml, string source, ConfigLayers layers)
    {
        // Nothing to merge: keep agent.yaml exactly as written.
        if (!UserSettings.ChatSections.Any(s => HasKey(layers.Defaults, s) || HasKey(layers.Overrides, s)))
            return agentYaml;
        var root = RackaYaml.LoadMapping(agentYaml, source);
        foreach (var section in UserSettings.ChatSections)
        {
            var key = new YamlScalarNode(section);
            YamlNode? node = null;
            foreach (var layer in new[] { layers.Defaults, root, layers.Overrides })
            {
                if (layer != null && layer.Children.TryGetValue(key, out var value))
                    node = node == null ? value : RackaYaml.Merge(node, value);
            }
            if (node != null)
                root.Children[key] = node;
        }
        return RackaYaml.Save(root);

        static bool HasKey(YamlMappingNode? mapping, string key) => mapping?.Children.ContainsKey(new YamlScalarNode(key)) == true;
    }
}

/// <summary>YAML layers around an agent's own agent.yaml: user settings below it, command line overrides above it.</summary>
public sealed record ConfigLayers(YamlMappingNode? Defaults = null, YamlMappingNode? Overrides = null)
{
    public static ConfigLayers From(UserSettings settings, YamlMappingNode? overrides = null) => new(settings.Root, overrides);
}
