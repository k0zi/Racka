using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Racka.Core.Llm;
using Racka.Core.Tools;

namespace Racka.Providers.Anthropic;

public sealed record AnthropicProviderOptions(
    string Model,
    int MaxTokens = 16000,
    string? Effort = null,
    bool ServerSideFallbacks = true);

/// <summary>
/// Claude Messages API provider (official Anthropic SDK, beta endpoint so that server-side
/// refusal fallbacks are available). Adaptive thinking is used implicitly by current models;
/// thinking blocks are kept verbatim in the append-only history.
/// </summary>
public sealed class AnthropicProvider : ILlmProvider
{
    public const string DefaultModel = "claude-opus-5-5";
    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    private static readonly HashSet<string> FallbackModels =
        ["claude-opus-5-5", "claude-opus-5", "claude-fable-5-1", "claude-sonnet-5-5"];

    private readonly IAnthropicClient _client;
    private readonly AnthropicProviderOptions _options;

    public AnthropicProvider(IAnthropicClient client, AnthropicProviderOptions options)
    {
        _client = client;
        _options = options;
    }

    public IConversation CreateConversation(string systemPrompt) => new AnthropicConversation(systemPrompt);

    public async Task<LlmTurn> NextTurnAsync(IConversation conversation, IReadOnlyList<IRackaTool> tools, CancellationToken ct)
    {
        var conv = (AnthropicConversation)conversation;
        var parameters = BuildParams(conv, tools);
        BetaMessage response = await _client.Beta.Messages.Create(parameters, ct);

        if (response.StopReason == "refusal")
        {
            var detail = response.StopDetails is { } d ? $" ({d.Category}: {d.Explanation})" : "";
            throw new LlmRefusalException($"The model declined the request{detail}.");
        }

        var assistantBlocks = new List<BetaContentBlockParam>();
        var text = new List<string>();
        var calls = new List<ToolCall>();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var t))
            {
                assistantBlocks.Add(new BetaTextBlockParam { Text = t.Text });
                text.Add(t.Text);
            }
            else if (block.TryPickThinking(out var thinking))
                assistantBlocks.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
            else if (block.TryPickRedactedThinking(out var redacted))
                assistantBlocks.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
            else if (block.TryPickFallback(out var fallback))
                assistantBlocks.Add(new BetaFallbackBlockParam
                {
                    From = new BetaFallbackInfoParam { Model = fallback.From.Model },
                    To = new BetaFallbackInfoParam { Model = fallback.To.Model },
                    Trigger = JsonSerializer.SerializeToElement(fallback.Trigger),
                });
            else if (block.TryPickToolUse(out var toolUse))
            {
                assistantBlocks.Add(new BetaToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
                calls.Add(new ToolCall(toolUse.ID, toolUse.Name, JsonSerializer.SerializeToElement(toolUse.Input)));
            }
        }

        conv.AddAssistant(assistantBlocks);
        var usage = new LlmUsage(response.Usage.InputTokens, response.Usage.OutputTokens);
        return new LlmTurn(string.Join("\n", text), calls, response.StopReason?.ToString() ?? "", usage);
    }

    internal MessageCreateParams BuildParams(AnthropicConversation conv, IReadOnlyList<IRackaTool> tools)
    {
        var parameters = new MessageCreateParams
        {
            Model = _options.Model,
            MaxTokens = _options.MaxTokens,
            System = new List<BetaTextBlockParam>
            {
                new() { Text = conv.SystemPrompt, CacheControl = new BetaCacheControlEphemeral() }
            },
            // Top-level automatic caching moves the breakpoint forward with the conversation.
            CacheControl = new BetaCacheControlEphemeral(),
            Tools = tools.Select(ToToolUnion).ToList(),
            Messages = conv.Messages.ToList(),
        };
        // Optional fields are only set when used: a null assignment is still sent as JSON null.
        if (_options.Effort is { } effort)
            parameters = parameters with { OutputConfig = new BetaOutputConfig { Effort = ParseEffort(effort) } };
        if (_options.ServerSideFallbacks && FallbackModels.Contains(_options.Model))
            parameters = parameters with { Betas = [FallbackBeta], Fallbacks = new Default() };
        return parameters;
    }

    private static BetaToolUnion ToToolUnion(IRackaTool tool)
    {
        var schema = tool.InputSchema.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        return new BetaTool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = InputSchema.FromRawUnchecked(schema),
        };
    }

    internal static Effort ParseEffort(string effort) => effort.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => throw new ArgumentException($"Unknown effort '{effort}'. Use low, medium, high, xhigh or max.")
    };
}

/// <summary>Append-only Claude conversation in native beta message types.</summary>
public sealed class AnthropicConversation(string systemPrompt) : IConversation
{
    private readonly List<BetaMessageParam> _messages = new();

    public string SystemPrompt { get; } = systemPrompt;
    public IReadOnlyList<BetaMessageParam> Messages => _messages;

    public void AddUserText(string text) =>
        AddUserBlocks([new BetaTextBlockParam { Text = text }]);

    public void AddToolResults(IReadOnlyList<ToolCallResult> results) =>
        AddUserBlocks(results.Select(r => (BetaContentBlockParam)new BetaToolResultBlockParam
        {
            ToolUseID = r.Call.Id,
            Content = r.Result.Content,
            IsError = r.Result.IsError,
        }).ToList());

    internal void AddAssistant(List<BetaContentBlockParam> blocks)
    {
        if (blocks.Count == 0)
            blocks.Add(new BetaTextBlockParam { Text = "(no response)" });
        _messages.Add(new BetaAssistantMessageParam { Content = blocks });
    }

    private void AddUserBlocks(List<BetaContentBlockParam> blocks)
    {
        // Two user turns in a row (e.g. tool results of an aborted run, then a new task) are sent
        // as a new user message; the API accepts consecutive user messages and merges them.
        _messages.Add(new BetaUserMessageParam { Content = blocks });
    }
}
