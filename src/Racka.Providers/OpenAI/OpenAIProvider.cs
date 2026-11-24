using System.Text.Json;
using OpenAI.Chat;
using Racka.Core.Llm;
using Racka.Core.Tools;

namespace Racka.Providers.OpenAI;

public sealed record OpenAIProviderOptions(int MaxTokens = 16000, string? Effort = null);

/// <summary>
/// Chat Completions provider (official OpenAI SDK). Works with any OpenAI compatible
/// endpoint (OpenAI, Azure, Ollama, vLLM, LM Studio) through OpenAIClientOptions.Endpoint.
/// </summary>
public sealed class OpenAIProvider(ChatClient client, OpenAIProviderOptions options) : ILlmProvider
{
    public IConversation CreateConversation(string systemPrompt) => new OpenAIConversation(systemPrompt);

    public async Task<LlmTurn> NextTurnAsync(IConversation conversation, IReadOnlyList<IRackaTool> tools, CancellationToken ct)
    {
        var conv = (OpenAIConversation)conversation;
        var chatOptions = new ChatCompletionOptions { MaxOutputTokenCount = options.MaxTokens };
#pragma warning disable OPENAI001 // reasoning effort is marked experimental in the SDK
        if (options.Effort is { } effort)
            chatOptions.ReasoningEffortLevel = new ChatReasoningEffortLevel(effort);
#pragma warning restore OPENAI001
        foreach (var tool in tools)
            chatOptions.Tools.Add(ChatTool.CreateFunctionTool(tool.Name, tool.Description, BinaryData.FromString(tool.InputSchema.GetRawText())));

        ChatCompletion completion = await client.CompleteChatAsync(conv.Messages, chatOptions, ct);

        if (completion.FinishReason == ChatFinishReason.ContentFilter)
            throw new LlmRefusalException("The response was blocked by the content filter.");

        conv.Add(new AssistantChatMessage(completion));
        var text = string.Concat(completion.Content.Where(p => p.Kind == ChatMessageContentPartKind.Text).Select(p => p.Text));
        var calls = completion.ToolCalls
            .Select(c => new ToolCall(c.Id, c.FunctionName, ParseArguments(c.FunctionArguments)))
            .ToList();
        var usage = completion.Usage is { } u ? new LlmUsage(u.InputTokenCount, u.OutputTokenCount) : null;
        return new LlmTurn(text, calls, completion.FinishReason.ToString(), usage);
    }

    internal static JsonElement ParseArguments(BinaryData arguments)
    {
        try
        {
            var text = arguments.ToString();
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Some local models emit invalid JSON; the tool will report the missing arguments.
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }
}

public sealed class OpenAIConversation : IConversation
{
    private readonly List<ChatMessage> _messages = new();

    public OpenAIConversation(string systemPrompt) => _messages.Add(new SystemChatMessage(systemPrompt));

    public IReadOnlyList<ChatMessage> Messages => _messages;

    public void AddUserText(string text) => _messages.Add(new UserChatMessage(text));

    public void AddToolResults(IReadOnlyList<ToolCallResult> results)
    {
        foreach (var r in results)
            _messages.Add(new ToolChatMessage(r.Call.Id, r.Result.IsError ? "ERROR: " + r.Result.Content : r.Result.Content));
    }

    internal void Add(ChatMessage message) => _messages.Add(message);
}
