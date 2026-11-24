using YamlDotNet.Serialization;

namespace Racka.Core.Config;

public enum EngineKind
{
    ClaudeCode,
    Codex,
    Anthropic,
    OpenAI
}

public static class EngineKindExtensions
{
    /// <summary>CLI engines run their own tool loop and reach Racka tools through the bridge MCP server.</summary>
    public static bool IsCli(this EngineKind kind) => kind is EngineKind.ClaudeCode or EngineKind.Codex;
}

public sealed class AgentConfig
{
    public string Name { get; set; } = "";
    public string Engine { get; set; } = "anthropic";
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public int MaxTokens { get; set; } = 16000;
    public int MaxIterations { get; set; } = 20;
    public string Workdir { get; set; } = "./workspace";
    public string SharedSkillsDir { get; set; } = "../../skills";
    public List<string> Skills { get; set; } = new();
    public Dictionary<string, McpServerConfig> McpServers { get; set; } = new();
    public AnthropicOptions Anthropic { get; set; } = new();
    [YamlMember(Alias = "openai")]
    public OpenAIOptions OpenAI { get; set; } = new();
    public ClaudeCodeOptions ClaudeCode { get; set; } = new();
    public CodexOptions Codex { get; set; } = new();
    public MemoryOptions Memory { get; set; } = new();
    public IrcConfig? Irc { get; set; }
    public DiscordConfig? Discord { get; set; }
    public SlackConfig? Slack { get; set; }
    public TeamsConfig? Teams { get; set; }
    public TelegramConfig? Telegram { get; set; }

    /// <summary>The chat sections that are configured and not switched off with <c>enabled: false</c>.</summary>
    [YamlIgnore]
    public IReadOnlyList<ChatConfig> ChatConnections =>
        new ChatConfig?[] { Irc, Discord, Slack, Teams, Telegram }.OfType<ChatConfig>().Where(c => c.Enabled).ToList();

    [YamlIgnore]
    public EngineKind EngineKind => Engine.ToLowerInvariant() switch
    {
        "claude-code" or "claudecode" => EngineKind.ClaudeCode,
        "codex" => EngineKind.Codex,
        "anthropic" or "claude" => EngineKind.Anthropic,
        "openai" => EngineKind.OpenAI,
        _ => throw new ConfigException($"Unknown engine '{Engine}'. Use claude-code, codex, anthropic or openai.")
    };
}

public sealed class AnthropicOptions
{
    public string? BaseUrl { get; set; }
    public string ApiKeyEnv { get; set; } = "ANTHROPIC_API_KEY";
    /// <summary>On a policy refusal the API retries on a substitute model within the same call.</summary>
    public bool ServerSideFallbacks { get; set; } = true;
}

public sealed class OpenAIOptions
{
    public string? BaseUrl { get; set; }
    public string ApiKeyEnv { get; set; } = "OPENAI_API_KEY";
}

public sealed class ClaudeCodeOptions
{
    public string Executable { get; set; } = "claude";
    public string? PermissionMode { get; set; }
    public List<string> AllowedTools { get; set; } = new();
    public List<string> DisallowedTools { get; set; } = new();
    public int? MaxTurns { get; set; }
    public decimal? MaxBudgetUsd { get; set; }
    /// <summary>Only load the MCP servers configured for this agent, not the user's global ones.</summary>
    public bool StrictMcpConfig { get; set; } = true;
    public List<string> ExtraArgs { get; set; } = new();
}

public sealed class CodexOptions
{
    public string Executable { get; set; } = "codex";
    /// <summary>read-only | workspace-write | danger-full-access</summary>
    public string Sandbox { get; set; } = "workspace-write";
    /// <summary>untrusted | on-failure | on-request | never. Nobody can answer a prompt in exec mode, so the default is never.</summary>
    public string ApprovalPolicy { get; set; } = "never";
    /// <summary>Marks the work directory as a trusted project for this run only (nothing is written to the Codex config).</summary>
    public bool TrustWorkdir { get; set; } = true;
    public bool SkipGitRepoCheck { get; set; } = true;
    /// <summary>Skips ~/.codex/config.toml (and its MCP servers); authentication still comes from CODEX_HOME.</summary>
    public bool IgnoreUserConfig { get; set; }
    /// <summary>Extra `-c key=value` overrides; values are TOML, e.g. model_verbosity: '"low"'.</summary>
    public Dictionary<string, string> Config { get; set; } = new();
    public List<string> ExtraArgs { get; set; } = new();
}

public sealed class MemoryOptions
{
    public bool Enabled { get; set; } = true;
    public string Dir { get; set; } = "./memory";
    /// <summary>The memory index in the system prompt is cut here; the rest is reachable with memory_search.</summary>
    public int MaxIndexChars { get; set; } = 8000;
}

public sealed class McpServerConfig
{
    public string? Command { get; set; }
    public List<string> Args { get; set; } = new();
    public Dictionary<string, string> Env { get; set; } = new();
    public string? Url { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();

    [YamlIgnore]
    public bool IsStdio => !string.IsNullOrEmpty(Command);
}

/// <summary>Settings every chat platform shares: who may give tasks, where events go and how replies look.</summary>
public abstract class ChatConfig
{
    /// <summary>Configuration key of the platform: irc, discord, slack, teams or telegram.</summary>
    [YamlIgnore]
    public abstract string Platform { get; }

    /// <summary>false switches off a connection inherited from the user settings.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>IRC: channels to join. Elsewhere: channel ids to listen in; empty means every channel the bot is in.</summary>
    public List<string> Channels { get; set; } = new();
    /// <summary>Where lifecycle events (online, task started/done/failed) and chat_report messages go.</summary>
    public string? ReportChannel { get; set; }
    /// <summary>Who may give tasks and commands. IRC: nick!user@host masks. Elsewhere: user ids.</summary>
    public List<string> Operators { get; set; } = new();
    public string CommandPrefix { get; set; } = "!";
    public int MaxReplyLines { get; set; } = 40;

    /// <summary>The report channel, or the first channel when none is set.</summary>
    [YamlIgnore]
    public string? ReportTarget => ReportChannel ?? Channels.FirstOrDefault();

    /// <summary>Replaces {agent} (the agent name) and {AGENT} (upper case, '-' as '_') in the name-like fields.</summary>
    public virtual void ApplyAgentName(string agentName) { }

    protected static string? Expand(string? value, string agentName) =>
        value?.Replace("{agent}", agentName, StringComparison.Ordinal)
            .Replace("{AGENT}", agentName.ToUpperInvariant().Replace('-', '_'), StringComparison.Ordinal);
}

public sealed class IrcConfig : ChatConfig
{
    public IrcConfig() => MaxReplyLines = 8;

    public override string Platform => "irc";
    public string Server { get; set; } = "";
    public int Port { get; set; } = 6697;
    public bool Tls { get; set; } = true;
    /// <summary>Defaults to the agent name.</summary>
    public string Nick { get; set; } = "";
    public string? User { get; set; }
    public string? RealName { get; set; }
    public string? SaslPasswordEnv { get; set; }

    public override void ApplyAgentName(string agentName)
    {
        Nick = string.IsNullOrWhiteSpace(Nick) ? agentName : Expand(Nick, agentName)!;
        User = Expand(User, agentName);
        RealName = Expand(RealName, agentName);
        SaslPasswordEnv = Expand(SaslPasswordEnv, agentName);
    }
}

public sealed class DiscordConfig : ChatConfig
{
    public override string Platform => "discord";
    public string TokenEnv { get; set; } = "{AGENT}_DISCORD_TOKEN";

    public override void ApplyAgentName(string agentName) => TokenEnv = Expand(TokenEnv, agentName)!;
}

public sealed class SlackConfig : ChatConfig
{
    public override string Platform => "slack";
    /// <summary>Bot token (xoxb-…) for the Web API.</summary>
    public string BotTokenEnv { get; set; } = "{AGENT}_SLACK_BOT_TOKEN";
    /// <summary>App-level token (xapp-…) with connections:write, for Socket Mode.</summary>
    public string AppTokenEnv { get; set; } = "{AGENT}_SLACK_APP_TOKEN";

    public override void ApplyAgentName(string agentName)
    {
        BotTokenEnv = Expand(BotTokenEnv, agentName)!;
        AppTokenEnv = Expand(AppTokenEnv, agentName)!;
    }
}

public sealed class TeamsConfig : ChatConfig
{
    public override string Platform => "teams";
    /// <summary>Microsoft App ID of the Azure Bot registration.</summary>
    public string AppId { get; set; } = "";
    /// <summary>Entra tenant of the app registration (single tenant bots).</summary>
    public string TenantId { get; set; } = "";
    public string ClientSecretEnv { get; set; } = "{AGENT}_TEAMS_CLIENT_SECRET";
    /// <summary>Address of the HTTP listener for /api/messages; put a tunnel or an HTTPS reverse proxy in front of it.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3978;

    public override void ApplyAgentName(string agentName)
    {
        AppId = Expand(AppId, agentName)!;
        ClientSecretEnv = Expand(ClientSecretEnv, agentName)!;
    }
}

public sealed class TelegramConfig : ChatConfig
{
    public override string Platform => "telegram";
    public string TokenEnv { get; set; } = "{AGENT}_TELEGRAM_TOKEN";

    public override void ApplyAgentName(string agentName) => TokenEnv = Expand(TokenEnv, agentName)!;
}

public sealed class ConfigException(string message) : Exception(message);
