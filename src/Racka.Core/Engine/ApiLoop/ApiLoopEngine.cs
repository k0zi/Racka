using HerdFlow;
using Racka.Core.Agents;
using Racka.Core.Llm;
using Racka.Core.Tools;

namespace Racka.Core.Engine.ApiLoop;

/// <summary>
/// Runs tasks against a chat model API with a HerdFlow tool loop:
/// BuildPrompt → LlmTurn ─tool_calls→ ToolExec → LlmTurn …, ─done→ Finalize, ─limit→ IterationLimit.
/// Conversations are kept per conversation key, so follow-up tasks see the earlier exchange.
/// </summary>
public sealed class ApiLoopEngine : IAgentEngine
{
    private readonly AgentDefinition _agent;
    private readonly ILlmProvider _provider;
    private readonly IReadOnlyList<IRackaTool> _tools;
    private readonly IAgentEvents _events;
    private readonly PromptOptions _promptOptions;
    private readonly Dictionary<string, IConversation> _conversations = new();
    private readonly AsyncFlow _flow;

    public ApiLoopEngine(AgentDefinition agent, ILlmProvider provider, IReadOnlyList<IRackaTool> tools,
        IAgentEvents events, PromptOptions promptOptions)
    {
        _agent = agent;
        _provider = provider;
        _tools = tools;
        _events = events;
        _promptOptions = promptOptions;
        _flow = BuildFlow(provider);
    }

    internal static AsyncFlow BuildFlow(ILlmProvider provider)
    {
        var build = new BuildPromptNode();
        var turn = new LlmTurnNode(provider);
        var exec = new ToolExecNode();
        var finalize = new FinalizeNode();
        var limit = new IterationLimitNode();

        build.Then(turn);
        turn.On(ApiLoopActions.ToolCalls).Then(exec);
        exec.Then(turn);
        turn.On(ApiLoopActions.Done).Then(finalize);
        turn.On(ApiLoopActions.Limit).Then(limit);
        return new AsyncFlow(build);
    }

    public async Task<AgentResult> RunAsync(AgentTask task, CancellationToken ct)
    {
        if (!_conversations.TryGetValue(task.ConversationKey, out var conversation))
        {
            // Built per conversation, so it includes memories saved since the agent started.
            conversation = _provider.CreateConversation(PromptBuilder.BuildSystemPrompt(_agent, _promptOptions));
            _conversations[task.ConversationKey] = conversation;
        }

        var ctx = new ApiLoopContext
        {
            Agent = _agent,
            Task = task,
            Conversation = conversation,
            Tools = _tools,
            Events = _events,
            CancellationToken = ct
        };
        try
        {
            await _flow.RunAsync(ctx);
            return new AgentResult(task.Id, !ctx.HitIterationLimit, ctx.Output ?? "",
                ctx.HitIterationLimit ? "iteration limit reached" : null,
                new AgentUsage(ctx.InputTokens, ctx.OutputTokens));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The conversation may now end with unanswered tool calls; start fresh next time.
            _conversations.Remove(task.ConversationKey);
            return AgentResult.Failed(task.Id, "cancelled");
        }
        catch (Exception e)
        {
            _conversations.Remove(task.ConversationKey);
            return AgentResult.Failed(task.Id, $"{e.GetType().Name}: {e.Message}");
        }
    }

    public void ResetConversation(string conversationKey) => _conversations.Remove(conversationKey);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
