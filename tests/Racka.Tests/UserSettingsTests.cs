using Racka.Core.Agents;
using Racka.Core.Config;
using Racka.Core.Messaging;
using Racka.Host;

namespace Racka.Tests;

public class UserSettingsTests
{
    private const string Soul = "You are a test racka.";

    private static AgentDefinition LoadAgent(TempDir dir, string agentYaml, string? userYaml, string? overridesYaml = null, string name = "lamb")
    {
        dir.Write($"agents/{name}/agent.yaml", agentYaml);
        dir.Write($"agents/{name}/soul.md", Soul);
        var settings = userYaml == null ? UserSettings.Empty : UserSettings.Load(dir.Write("config.yaml", userYaml));
        var overrides = overridesYaml == null ? null : RackaYaml.LoadMapping(overridesYaml, "overrides");
        return AgentDefinition.Load(Path.Combine(dir.Path, "agents", name), _ => null, ConfigLayers.From(settings, overrides));
    }

    [Fact]
    public void AgentInheritsChatSectionsFromUserSettings()
    {
        using var dir = new TempDir();
        var agent = LoadAgent(dir, "engine: anthropic\n", """
            runtimeDir: /tmp/racka-run
            irc:
              server: irc.libera.chat
              channels: ["#racka"]
              operators: ["k0zi!*@home"]
            telegram:
              operators: ["42"]
            """);

        var irc = agent.Config.Irc!;
        Assert.Equal("irc.libera.chat", irc.Server);
        Assert.Equal(["#racka"], irc.Channels);
        Assert.Equal("lamb", irc.Nick); // no nick: the agent name
        Assert.Equal(8, irc.MaxReplyLines);
        Assert.Equal("LAMB_TELEGRAM_TOKEN", agent.Config.Telegram!.TokenEnv);
        Assert.Equal(["irc", "telegram"], agent.Config.ChatConnections.Select(c => c.Platform));
    }

    [Fact]
    public void AgentYamlOverridesFieldByFieldAndCanSwitchOff()
    {
        using var dir = new TempDir();
        var agent = LoadAgent(dir, """
            irc:
              nick: "{agent}-racka"
              channels: ["#lamb"]
            telegram:
              enabled: false
            """, """
            irc:
              server: irc.libera.chat
              port: 6667
              tls: false
              channels: ["#racka", "#other"]
            telegram:
              tokenEnv: SHARED
            """, name: "big-lamb");

        var irc = agent.Config.Irc!;
        Assert.Equal("irc.libera.chat", irc.Server);
        Assert.Equal(6667, irc.Port);
        Assert.False(irc.Tls);
        Assert.Equal("big-lamb-racka", irc.Nick);
        Assert.Equal(["#lamb"], irc.Channels); // lists are replaced, not merged
        Assert.False(agent.Config.Telegram!.Enabled);
        Assert.Equal(["irc"], agent.Config.ChatConnections.Select(c => c.Platform));
    }

    [Fact]
    public void CommandLineOverridesWin()
    {
        using var dir = new TempDir();
        var agent = LoadAgent(dir, "irc: { server: agent.example, nick: lamb1 }\n", "irc: { server: user.example, port: 7000 }\n",
            "irc: { server: cli.example, channels: ['#cli'] }\ndiscord: { enabled: false }\n");

        Assert.Equal("cli.example", agent.Config.Irc!.Server);
        Assert.Equal(7000, agent.Config.Irc.Port);
        Assert.Equal("lamb1", agent.Config.Irc.Nick);
        Assert.Equal(["#cli"], agent.Config.Irc.Channels);
        Assert.False(agent.Config.Discord!.Enabled);
    }

    [Fact]
    public void ExpandsAgentPlaceholdersInEnvNames()
    {
        using var dir = new TempDir();
        var agent = LoadAgent(dir, "engine: anthropic\n", """
            slack: {}
            teams: { appId: app, tenantId: tenant }
            irc: { server: x, saslPasswordEnv: "{AGENT}_IRC_PASSWORD", realName: "Racka {agent}" }
            """, name: "my-lamb");

        Assert.Equal("MY_LAMB_SLACK_BOT_TOKEN", agent.Config.Slack!.BotTokenEnv);
        Assert.Equal("MY_LAMB_SLACK_APP_TOKEN", agent.Config.Slack.AppTokenEnv);
        Assert.Equal("MY_LAMB_TEAMS_CLIENT_SECRET", agent.Config.Teams!.ClientSecretEnv);
        Assert.Equal("MY_LAMB_IRC_PASSWORD", agent.Config.Irc!.SaslPasswordEnv);
        Assert.Equal("Racka my-lamb", agent.Config.Irc.RealName);
    }

    [Fact]
    public void ValidatesMergedSections()
    {
        using var dir = new TempDir();
        var e = Assert.Throws<ConfigException>(() => LoadAgent(dir, "irc: { nick: x }\n", "irc: { channels: ['#a'] }\n"));
        Assert.Contains("irc.server", e.Message);

        Assert.Contains("teams.appId", Assert.Throws<ConfigException>(() => LoadAgent(dir, "teams: {}\n", null)).Message);
        // A switched off section is not validated.
        Assert.Empty(LoadAgent(dir, "irc: { enabled: false }\n", null).Config.ChatConnections);
    }

    [Fact]
    public void ExampleAgentsLoadTheSameThroughTheMerge()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../agents"));
        var settings = new ConfigLayers(RackaYaml.LoadMapping("irc: { enabled: false }\n", "test"));
        foreach (var dir in Directory.GetDirectories(root))
        {
            var plain = AgentDefinition.Load(dir, _ => "x").Config;
            var merged = AgentDefinition.Load(dir, _ => "x", settings).Config;
            merged.Irc = plain.Irc; // the only intended difference
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(plain), System.Text.Json.JsonSerializer.Serialize(merged));
        }
    }

    [Fact]
    public void RejectsUnknownKeysInUserSettings()
    {
        using var dir = new TempDir();
        var path = dir.Write("config.yaml", "irc:\n  servr: x\n");
        Assert.Throws<ConfigException>(() => UserSettings.Load(path));
    }

    [Fact]
    public void CreatesTheTemplateOnceWithPrivatePermissions()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "racka", "config.yaml");

        Assert.True(UserSettings.EnsureCreated(path));
        Assert.False(UserSettings.EnsureCreated(path));
        var settings = UserSettings.Load(path); // the template is all comments
        Assert.Null(settings.RuntimeDir);
        Assert.Empty(settings.Root.Children);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(path)!));
        }
    }

    [Fact]
    public void TemplateSectionsAreValidWhenUncommented()
    {
        using var dir = new TempDir();
        // Section lines are "# key:" and "#   nested"; everything else in the template is prose.
        var uncommented = string.Join('\n', UserSettings.Template.Split('\n')
            .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^# [A-Za-z]+:|^#   "))
            .Select(l => l[2..]));
        var settings = UserSettings.Load(dir.Write("config.yaml", uncommented));
        Assert.Equal(5, UserSettings.ChatSections.Count(s => settings.Root.Children.ContainsKey(new YamlDotNet.RepresentationModel.YamlScalarNode(s))));
    }

    [Fact]
    public void RuntimeDirPrecedence()
    {
        Assert.Equal("/from/cli", BusPaths.Resolve("/from/cli", "/from/settings").RuntimeDirectory);
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RACKA_RUNTIME_DIR")))
        {
            Assert.Equal("/from/settings", BusPaths.Resolve(null, "/from/settings").RuntimeDirectory);
            Assert.Equal(new BusPaths().RuntimeDirectory, BusPaths.Resolve(null, null).RuntimeDirectory);
        }
    }
}

public class ConfigEditorTests
{
    [Fact]
    public void SetsListsAndScalarsAndKeepsTheFileValid()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "cfg", "config.yaml");
        var editor = ConfigEditor.OpenUserSettings(path);
        editor.Set("irc.server", "irc.libera.chat");
        editor.Set("irc.port", "6697");
        editor.Set("irc.channels", "#racka, #racka-status");
        editor.Set("irc.nick", "{agent}-racka");
        editor.Set("runtimeDir", "~/run");
        editor.Save();

        var settings = UserSettings.Load(path);
        Assert.EndsWith("run", settings.RuntimeDir);
        Assert.DoesNotContain("~", settings.RuntimeDir);
        var reopened = ConfigEditor.OpenUserSettings(path);
        Assert.Equal("#racka,#racka-status", reopened.Get("irc.channels"));
        Assert.Equal("{agent}-racka", reopened.Get("irc.nick"));
        Assert.Equal("6697", reopened.Get("irc.port"));
        Assert.Null(reopened.Get("irc.reportChannel"));

        var irc = RackaYaml.LiteralDeserializer.Deserialize<UserSettingsFile>(File.ReadAllText(path)).Irc!;
        Assert.Equal(["#racka", "#racka-status"], irc.Channels);
        Assert.Equal("{agent}-racka", irc.Nick);
    }

    [Fact]
    public void RejectsUnknownKeysAndBadValues()
    {
        using var dir = new TempDir();
        var editor = ConfigEditor.OpenUserSettings(Path.Combine(dir.Path, "config.yaml"));

        var unknown = Assert.Throws<ConfigException>(() => editor.Set("irc.servr", "x"));
        Assert.Contains("irc.servr", unknown.Message);
        Assert.Contains("server", unknown.Message);
        Assert.Throws<ConfigException>(() => editor.Set("engine", "codex")); // agent.yaml only
        Assert.Contains("number", Assert.Throws<ConfigException>(() => editor.Set("irc.port", "many")).Message);
        Assert.Contains("true or false", Assert.Throws<ConfigException>(() => editor.Set("irc.tls", "yes please")).Message);
        Assert.Contains("section", Assert.Throws<ConfigException>(() => editor.Set("irc", "x")).Message);
    }

    [Fact]
    public void UnsetRemovesEmptySections()
    {
        using var dir = new TempDir();
        var path = dir.Write("config.yaml", "irc:\n  server: x\ntelegram:\n  operators: ['1']\n");
        var editor = ConfigEditor.OpenUserSettings(path);

        Assert.True(editor.Unset("irc.server"));
        Assert.False(editor.Unset("irc.server"));
        editor.Save();

        Assert.Equal("telegram:\n  operators: ['1']\n", File.ReadAllText(path).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void EditsAnAgentYaml()
    {
        using var dir = new TempDir();
        dir.Write("agents/lamb/agent.yaml", "name: lamb\nengine: anthropic\n");
        var editor = ConfigEditor.OpenAgent(Path.Combine(dir.Path, "agents", "lamb"));
        editor.Set("telegram.enabled", "false");
        editor.Set("mcpServers.fs.command", "npx");
        editor.Set("openai.baseUrl", "http://localhost:11434/v1");
        editor.Save();

        var config = AgentDefinition.ParseConfig(File.ReadAllText(editor.Path), _ => null);
        Assert.Equal("anthropic", config.Engine);
        Assert.False(config.Telegram!.Enabled);
        Assert.Equal("npx", config.McpServers["fs"].Command);
        Assert.Equal("http://localhost:11434/v1", config.OpenAI.BaseUrl);
    }
}
