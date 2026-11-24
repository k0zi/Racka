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

public sealed class IrcConfig
{
    public string Server { get; set; } = "";
    public int Port { get; set; } = 6697;
    public bool Tls { get; set; } = true;
    public string Nick { get; set; } = "";
    public string? User { get; set; }
    public string? RealName { get; set; }
    public List<string> Channels { get; set; } = new();
    public string? ReportChannel { get; set; }
    public List<string> Operators { get; set; } = new();
    public string? SaslPasswordEnv { get; set; }
    public string CommandPrefix { get; set; } = "!";
    public int MaxReplyLines { get; set; } = 8;
}

public sealed class ConfigException(string message) : Exception(message);
