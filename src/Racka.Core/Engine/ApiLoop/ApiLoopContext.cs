using Racka.Core.Agents;
using Racka.Core.Llm;
using Racka.Core.Tools;

namespace Racka.Core.Engine.ApiLoop;

/// <summary>The HerdFlow shared state for one task run of the API loop engine.</summary>
public sealed class ApiLoopContext
{
    public required AgentDefinition Agent { get; init; }
    public required AgentTask Task { get; init; }
    public required IConversation Conversation { get; init; }
    public required IReadOnlyList<IRackaTool> Tools { get; init; }
    public required IAgentEvents Events { get; init; }
    public required CancellationToken CancellationToken { get; init; }

    public LlmTurn? LastTurn { get; set; }
    public int Iterations { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public string? Output { get; set; }
    public bool HitIterationLimit { get; set; }

    public ToolContext ToolContext => new(Agent, Task, Events);
}
