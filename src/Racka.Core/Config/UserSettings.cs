using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Racka.Core.Config;

/// <summary>Where the per-user settings live: $RACKA_CONFIG_DIR, else ~/.config/racka (Windows: %APPDATA%\racka).</summary>
public static class RackaDirectories
{
    public const string ConfigFileName = "config.yaml";

    public static string ConfigDirectory
    {
        get
        {
            var explicitDir = Environment.GetEnvironmentVariable("RACKA_CONFIG_DIR");
            if (!string.IsNullOrEmpty(explicitDir))
                return explicitDir;
            // ApplicationData is $XDG_CONFIG_HOME or ~/.config on Linux and macOS, %APPDATA% on Windows.
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), "racka");
        }
    }

    public static string ConfigFile => Path.Combine(ConfigDirectory, ConfigFileName);

    /// <summary>Expands a leading ~ to the home directory.</summary>
    public static string ExpandHome(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "")
            : path;
}

/// <summary>Schema of config.yaml. Only used to validate the file; agents read the chat sections as YAML layers.</summary>
public sealed class UserSettingsFile
{
    public string? RuntimeDir { get; set; }
    public IrcConfig? Irc { get; set; }
    public DiscordConfig? Discord { get; set; }
    public SlackConfig? Slack { get; set; }
    public TeamsConfig? Teams { get; set; }
    public TelegramConfig? Telegram { get; set; }
}

/// <summary>
/// The user's config.yaml: the runtime directory and chat connections every agent inherits. An agent's own
/// agent.yaml overrides them field by field, and `enabled: false` there switches a connection off for that agent.
/// </summary>
public sealed class UserSettings
{
    /// <summary>The top-level keys an agent inherits from the user settings.</summary>
    public static readonly IReadOnlyList<string> ChatSections = ["irc", "discord", "slack", "teams", "telegram"];

    private UserSettings(string path, YamlMappingNode root, UserSettingsFile typed)
    {
        Path = path;
        Root = root;
        RuntimeDir = typed.RuntimeDir is { Length: > 0 } dir ? RackaDirectories.ExpandHome(dir) : null;
    }

    public static UserSettings Empty { get; } = new("", new YamlMappingNode(), new UserSettingsFile());

    public string Path { get; }
    public string? RuntimeDir { get; }

    /// <summary>The whole file; agents take the <see cref="ChatSections"/> keys from it.</summary>
    public YamlMappingNode Root { get; }

    public static UserSettings Load(string? path = null)
    {
        path ??= RackaDirectories.ConfigFile;
        if (!File.Exists(path))
            return new UserSettings(path, new YamlMappingNode(), new UserSettingsFile());
        var text = File.ReadAllText(path);
        var root = RackaYaml.LoadMapping(text, path);
        UserSettingsFile typed;
        try
        {
            typed = RackaYaml.LiteralDeserializer.Deserialize<UserSettingsFile>(text) ?? new UserSettingsFile();
        }
        catch (YamlDotNet.Core.YamlException e)
        {
            throw new ConfigException($"Invalid {path}: {e.Message}");
        }
        return new UserSettings(path, root, typed);
    }

    /// <summary>Writes a commented template when the file does not exist yet. Returns true when it created one.</summary>
    public static bool EnsureCreated(string? path = null)
    {
        path ??= RackaDirectories.ConfigFile;
        if (File.Exists(path))
            return false;
        var dir = System.IO.Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(stream))
            writer.Write(Template);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return true;
    }

    public const string Template = """
        # Racka user settings. Every agent started by this user reads them.
        # Edit this file or use `racka config set <key> <value>` (that rewrites the file without these comments).
        #
        # Chat sections here are defaults for every agent; an agent's agent.yaml overrides them field by field,
        # and `enabled: false` in agent.yaml switches a connection off for that agent.
        # {agent} is replaced with the agent name, {AGENT} with the upper case name ('-' becomes '_').
        # Secrets never go here: *Env fields name the environment variable that holds them.

        # runtimeDir: ~/.racka/run             # agent sockets (default: $XDG_RUNTIME_DIR/racka)

        # irc:
        #   server: irc.libera.chat
        #   port: 6697
        #   tls: true
        #   nick: "{agent}-racka"              # default: the agent name
        #   channels: ["#racka"]
        #   reportChannel: "#racka-status"
        #   operators: ["yournick!*@your.host"]

        # telegram:                            # one bot per agent: tokens cannot be shared between agents
        #   tokenEnv: "{AGENT}_TELEGRAM_TOKEN"
        #   operators: ["123456789"]           # Telegram user ids

        # discord:
        #   tokenEnv: "{AGENT}_DISCORD_TOKEN"
        #   channels: []                       # channel ids; empty: every channel the bot can read
        #   operators: ["123456789012345678"]  # Discord user ids

        # slack:                               # Socket Mode app; one app per agent
        #   botTokenEnv: "{AGENT}_SLACK_BOT_TOKEN"
        #   appTokenEnv: "{AGENT}_SLACK_APP_TOKEN"
        #   operators: ["U0123456789"]         # Slack member ids

        # teams:                               # Azure Bot; /api/messages must be reachable over HTTPS
        #   appId: 00000000-0000-0000-0000-000000000000
        #   tenantId: 00000000-0000-0000-0000-000000000000
        #   clientSecretEnv: "{AGENT}_TEAMS_CLIENT_SECRET"
        #   port: 3978
        #   operators: ["00000000-0000-0000-0000-000000000000"]   # Entra object ids

        """;
}

/// <summary>Shared YAML plumbing for agent.yaml and config.yaml.</summary>
public static class RackaYaml
{
    /// <summary>Deserializer for the typed config, with ${VAR} expansion.</summary>
    public static IDeserializer Deserializer(Func<string, string?>? envLookup = null) =>
        new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithNodeDeserializer(new EnvExpandingStringDeserializer(envLookup), s => s.OnTop())
            .Build();

    /// <summary>Deserializer that leaves ${VAR} references alone; validates a file without needing its secrets.</summary>
    public static IDeserializer LiteralDeserializer { get; } =
        new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

    /// <summary>Parses a YAML document whose root is a mapping; an empty document gives an empty mapping.</summary>
    public static YamlMappingNode LoadMapping(string text, string source)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlDotNet.Core.YamlException e)
        {
            throw new ConfigException($"Invalid {source}: {e.Message}");
        }
        if (stream.Documents.Count == 0)
            return new YamlMappingNode();
        return stream.Documents[0].RootNode switch
        {
            YamlMappingNode mapping => mapping,
            YamlScalarNode { Value: null or "" } => new YamlMappingNode(),
            _ => throw new ConfigException($"Invalid {source}: the top level must be a mapping.")
        };
    }

    public static string Save(YamlMappingNode root)
    {
        if (root.Children.Count == 0)
            return "";
        var writer = new StringWriter();
        new YamlStream(new YamlDocument(root)).Save(writer, assignAnchors: false);
        var text = writer.ToString();
        // YamlStream ends every document with "..."; it is noise in a config file.
        return text.EndsWith("...\n", StringComparison.Ordinal) ? text[..^4] : text.Replace("\r\n...\r\n", "\r\n");
    }

    /// <summary>
    /// Returns <paramref name="baseNode"/> with <paramref name="overlay"/> merged in: mappings merge key by key,
    /// every other value (scalars, lists) is replaced. Neither input is modified.
    /// </summary>
    public static YamlNode Merge(YamlNode? baseNode, YamlNode overlay)
    {
        if (baseNode is not YamlMappingNode baseMap || overlay is not YamlMappingNode overlayMap)
            return overlay;
        var result = new YamlMappingNode();
        foreach (var (key, value) in baseMap.Children)
            result.Children[key] = value;
        foreach (var (key, value) in overlayMap.Children)
            result.Children[key] = result.Children.TryGetValue(key, out var existing) ? Merge(existing, value) : value;
        return result;
    }
}
