using System.ClientModel.Primitives;
using System.Text.Json;
using Anthropic;
using OpenAI;
using OpenAI.Chat;
using Racka.Core.Engine;
using Racka.Core.Engine.ApiLoop;
using Racka.Providers.Anthropic;
using Racka.Providers.OpenAI;

namespace Racka.Tests;

public class ProviderTests
{
    private static readonly PromptOptions Options = new(true, false, false);

    private const string AnthropicToolUse = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5",
         "content":[
           {"type":"thinking","thinking":"","signature":"sig-123"},
           {"type":"text","text":"Let me echo."},
           {"type":"tool_use","id":"toolu_1","name":"echo","input":{"text":"hi"}}],
         "stop_reason":"tool_use","stop_sequence":null,
         "usage":{"input_tokens":10,"output_tokens":5}}
        """;

    private const string AnthropicText = """
        {"id":"msg_2","type":"message","role":"assistant","model":"claude-opus-5-5",
         "content":[{"type":"text","text":"All done."}],
         "stop_reason":"end_turn","stop_sequence":null,
         "usage":{"input_tokens":20,"output_tokens":3}}
        """;

    [Fact]
    public async Task AnthropicRoundTripsThinkingAndToolResults()
    {
        var handler = new FakeHttpHandler(AnthropicToolUse, AnthropicText);
        var client = new AnthropicClient { ApiKey = "test-key", HttpClient = new HttpClient(handler), MaxRetries = 0 };
        var provider = new AnthropicProvider(client, new AnthropicProviderOptions("claude-opus-5-5", Effort: "high"));
        var engine = new ApiLoopEngine(TestAgents.Create(), provider, [new EchoTool()], NullAgentEvents.Instance, Options);

        var result = await engine.RunAsync(AgentTask.Create("say hi", TaskSources.Cli), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal("All done.", result.Output);
        Assert.Equal(30, result.Usage!.InputTokens);
        Assert.Equal(2, handler.Requests.Count);

        var (request, first) = handler.Requests[0];
        Assert.Contains("server-side-fallback-2026-07-01", request.Headers.GetValues("anthropic-beta").Single());
        Assert.Equal("default", first.GetProperty("fallbacks").GetString());
        Assert.Equal("high", first.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("ephemeral", first.GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal("ephemeral", first.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString());
        var tool = first.GetProperty("tools")[0];
        Assert.Equal("echo", tool.GetProperty("name").GetString());
        Assert.Equal("string", tool.GetProperty("input_schema").GetProperty("properties").GetProperty("text").GetProperty("type").GetString());

        var messages = handler.Requests[1].Body.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        var assistant = messages[1];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        Assert.Equal("sig-123", assistant.GetProperty("content")[0].GetProperty("signature").GetString());
        var toolResult = messages[2].GetProperty("content")[0];
        Assert.Equal("tool_result", toolResult.GetProperty("type").GetString());
        Assert.Equal("toolu_1", toolResult.GetProperty("tool_use_id").GetString());
    }

    [Fact]
    public async Task AnthropicRefusalFailsTheTask()
    {
        var refusal = """
            {"id":"msg_3","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],
             "stop_reason":"refusal","stop_sequence":null,
             "stop_details":{"type":"refusal","category":"cyber","explanation":"nope"},
             "usage":{"input_tokens":1,"output_tokens":1}}
            """;
        var client = new AnthropicClient { ApiKey = "k", HttpClient = new HttpClient(new FakeHttpHandler(refusal)), MaxRetries = 0 };
        var provider = new AnthropicProvider(client, new AnthropicProviderOptions("claude-opus-5-5"));
        var engine = new ApiLoopEngine(TestAgents.Create(), provider, [], NullAgentEvents.Instance, Options);

        var result = await engine.RunAsync(AgentTask.Create("x", TaskSources.Cli), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("declined", result.Error);
    }

    [Fact]
    public async Task AnthropicOlderModelSkipsFallbacks()
    {
        var handler = new FakeHttpHandler(AnthropicText);
        var client = new AnthropicClient { ApiKey = "k", HttpClient = new HttpClient(handler), MaxRetries = 0 };
        var provider = new AnthropicProvider(client, new AnthropicProviderOptions("claude-haiku-5-5"));
        var engine = new ApiLoopEngine(TestAgents.Create(), provider, [], NullAgentEvents.Instance, Options);

        await engine.RunAsync(AgentTask.Create("x", TaskSources.Cli), CancellationToken.None);

        Assert.False(handler.Requests[0].Body.TryGetProperty("fallbacks", out _));
    }

    [Fact]
    public async Task OpenAIRunsToolLoopAgainstCompatibleEndpoint()
    {
        var toolCall = """
            {"id":"c1","object":"chat.completion","created":1,"model":"llama",
             "choices":[{"index":0,"finish_reason":"tool_calls","message":{"role":"assistant","content":null,
               "tool_calls":[{"id":"call_1","type":"function","function":{"name":"echo","arguments":"{\"text\":\"hey\"}"}}]}}],
             "usage":{"prompt_tokens":7,"completion_tokens":2,"total_tokens":9}}
            """;
        var answer = """
            {"id":"c2","object":"chat.completion","created":2,"model":"llama",
             "choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"Echoed."}}],
             "usage":{"prompt_tokens":9,"completion_tokens":2,"total_tokens":11}}
            """;
        var handler = new FakeHttpHandler(toolCall, answer);
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri("http://localhost:11434/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(handler))
        };
        var provider = new OpenAIProvider(new ChatClient("llama", new System.ClientModel.ApiKeyCredential("x"), options), new OpenAIProviderOptions());
        var engine = new ApiLoopEngine(TestAgents.Create(), provider, [new EchoTool()], NullAgentEvents.Instance, Options);

        var result = await engine.RunAsync(AgentTask.Create("go", TaskSources.Cli), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal("Echoed.", result.Output);
        Assert.Equal("localhost", handler.Requests[0].Request.RequestUri!.Host);
        var messages = handler.Requests[1].Body.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Equal("echo:hey", messages[3].GetProperty("content").GetString());
    }

    [Fact]
    public void OpenAIInvalidArgumentsBecomeEmptyObject()
    {
        var args = OpenAIProvider.ParseArguments(BinaryData.FromString("{not json"));
        Assert.Equal(JsonValueKind.Object, args.ValueKind);
    }
}
