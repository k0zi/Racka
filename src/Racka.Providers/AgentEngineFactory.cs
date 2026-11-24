using System.ClientModel;
using Anthropic;
using OpenAI;
using OpenAI.Chat;
using Racka.Core.Agents;
using Racka.Core.Config;
using Racka.Core.Engine;
using Racka.Core.Engine.ApiLoop;
using Racka.Core.Llm;
using Racka.Core.Tools;
using Racka.Providers.Anthropic;
using Racka.Providers.ClaudeCode;
using Racka.Providers.Codex;
using Racka.Providers.OpenAI;

namespace Racka.Providers;

public static class AgentEngineFactory
{
    /// <param name="tools">Tools for the API engines (built-ins, MCP, skills). Ignored by the CLI engines, which reach them through the bridge.</param>
    public static IAgentEngine Create(AgentDefinition agent, IReadOnlyList<IRackaTool> tools, IAgentEvents events,
        PromptOptions promptOptions, BridgeCommand? bridge)
    {
        var config = agent.Config;
        switch (config.EngineKind)
        {
            case EngineKind.ClaudeCode:
                ClaudeCodeWorkspace.LinkSkills(agent);
                var mcpConfig = ClaudeCodeWorkspace.WriteMcpConfig(agent, bridge);
                return new ClaudeCodeEngine(agent, events, promptOptions with { IncludeSkillIndex = false }, mcpConfig);

            case EngineKind.Codex:
                // Skills are listed in the instructions and loaded with the bridged load_skill tool.
                return new CodexEngine(agent, events, promptOptions, bridge);

            case EngineKind.Anthropic:
                return new ApiLoopEngine(agent, CreateAnthropic(config), tools, events, promptOptions);

            case EngineKind.OpenAI:
                return new ApiLoopEngine(agent, CreateOpenAI(config), tools, events, promptOptions);

            default:
                throw new ConfigException($"Unsupported engine '{config.Engine}'.");
        }
    }

    private static ILlmProvider CreateAnthropic(AgentConfig config)
    {
        // Without an explicit key the SDK resolves credentials itself (ANTHROPIC_API_KEY, `ant auth login` profile, …).
        var apiKey = Environment.GetEnvironmentVariable(config.Anthropic.ApiKeyEnv);
        IAnthropicClient client = string.IsNullOrEmpty(apiKey) ? new AnthropicClient() : new AnthropicClient { ApiKey = apiKey };
        if (config.Anthropic.BaseUrl is { } baseUrl)
            client = client.WithOptions(o => o with { BaseUrl = baseUrl });

        return new AnthropicProvider(client, new AnthropicProviderOptions(
            config.Model ?? AnthropicProvider.DefaultModel,
            config.MaxTokens,
            config.Effort,
            config.Anthropic.ServerSideFallbacks));
    }

    private static ILlmProvider CreateOpenAI(AgentConfig config)
    {
        var model = config.Model ?? throw new ConfigException("The openai engine needs a 'model'.");
        var apiKey = Environment.GetEnvironmentVariable(config.OpenAI.ApiKeyEnv);
        if (string.IsNullOrEmpty(apiKey))
        {
            if (config.OpenAI.BaseUrl == null)
                throw new ConfigException($"Environment variable '{config.OpenAI.ApiKeyEnv}' is not set.");
            apiKey = "not-needed"; // local OpenAI compatible servers usually ignore the key
        }
        var options = new OpenAIClientOptions();
        if (config.OpenAI.BaseUrl is { } baseUrl)
            options.Endpoint = new Uri(baseUrl);
        var client = new ChatClient(model, new ApiKeyCredential(apiKey), options);
        return new OpenAIProvider(client, new OpenAIProviderOptions(config.MaxTokens, config.Effort));
    }
}
