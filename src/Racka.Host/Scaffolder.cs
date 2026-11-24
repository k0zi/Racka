using Racka.Core.Config;

namespace Racka.Host;

/// <summary>`racka new`: creates an agent directory with agent.yaml and soul.md templates.</summary>
internal static class Scaffolder
{
    public static string Create(string agentsRoot, string name, string engine)
    {
        var config = new AgentConfig { Name = name, Engine = engine };
        _ = config.EngineKind; // validates the engine name
        var dir = Path.GetFullPath(Path.Combine(agentsRoot, name));
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            throw new ConfigException($"'{dir}' already exists and is not empty.");

        Directory.CreateDirectory(Path.Combine(dir, "skills"));
        File.WriteAllText(Path.Combine(dir, "agent.yaml"), AgentYaml(name, config.EngineKind) + ChatYaml);
        File.WriteAllText(Path.Combine(dir, "soul.md"), Soul(name));
        File.WriteAllText(Path.Combine(dir, ".gitignore"), "state/\nworkspace/\nmemory/\n");
        return dir;
    }

    private static string AgentYaml(string name, EngineKind engine) => engine switch
    {
        EngineKind.ClaudeCode => $"""
            name: {name}
            engine: claude-code
            # model: claude-opus-5-5
            # effort: high
            workdir: ./workspace
            claudeCode:
              permissionMode: acceptEdits
              allowedTools: [Read, Grep, Glob]
              # maxBudgetUsd: 1.0
            mcpServers: {"{}"}
            skills: []
            # memory:              # long-term memory in ./memory (on by default)
            #   enabled: true
            #   maxIndexChars: 8000

            """,
        EngineKind.Codex => $"""
            name: {name}
            engine: codex
            # model: ...                    # default: the model set in ~/.codex/config.toml
            # effort: high
            workdir: ./workspace
            codex:
              sandbox: workspace-write      # read-only | workspace-write | danger-full-access
              approvalPolicy: never
              # ignoreUserConfig: false     # true: skip ~/.codex/config.toml and its MCP servers
            mcpServers: {"{}"}
            skills: []
            # memory:              # long-term memory in ./memory (on by default)
            #   enabled: true
            #   maxIndexChars: 8000

            """,
        EngineKind.OpenAI => $"""
            name: {name}
            engine: openai
            model: llama3.1
            openai:
              baseUrl: http://localhost:11434/v1
              apiKeyEnv: OPENAI_API_KEY
            maxIterations: 20
            mcpServers: {"{}"}
            skills: []
            # memory:              # long-term memory in ./memory (on by default)
            #   enabled: true
            #   maxIndexChars: 8000

            """,
        _ => $"""
            name: {name}
            engine: anthropic
            model: claude-opus-5-5
            effort: high
            maxIterations: 20
            mcpServers: {"{}"}
            skills: []
            # memory:              # long-term memory in ./memory (on by default)
            #   enabled: true
            #   maxIndexChars: 8000

            """
    };

    /// <summary>Chat sections; the same keys in the user settings (racka config) apply to every agent.</summary>
    private const string ChatYaml = """
        # Chat: these sections override the same ones in the user settings (racka config show); enabled: false switches one off.
        # irc:
        #   server: irc.libera.chat
        #   channels: ["#racka"]
        #   reportChannel: "#racka-status"
        #   operators: ["yournick!*@your.host"]
        # telegram: { tokenEnv: "{AGENT}_TELEGRAM_TOKEN", operators: ["123456789"] }
        # discord:  { tokenEnv: "{AGENT}_DISCORD_TOKEN", operators: ["123456789012345678"] }
        # slack:    { botTokenEnv: "{AGENT}_SLACK_BOT_TOKEN", appTokenEnv: "{AGENT}_SLACK_APP_TOKEN", operators: ["U0123456789"] }
        # teams:    { appId: "…", tenantId: "…", clientSecretEnv: "{AGENT}_TEAMS_CLIENT_SECRET", port: 3978 }

        """;

    private static string Soul(string name) => $"""
        # {char.ToUpperInvariant(name[0])}{name[1..]}

        You are {name}. Describe here who this agent is: its personality, tone,
        expertise, what it cares about and how it works with the other agents.

        ## Style
        - Concise and friendly.
        - Says clearly when something is outside its expertise.

        """;
}
