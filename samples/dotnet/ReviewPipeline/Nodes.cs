using HerdFlow;
using Racka.Core.Messaging;

namespace Racka.Samples.ReviewPipeline;

/// <summary>HerdFlow shared state. The flow clones nodes, so everything that changes lives here.</summary>
public sealed class PipelineState
{
    public required string Feature { get; init; }
    public required BusClient Bus { get; init; }
    public required string RunId { get; init; }
    public required CancellationToken CancellationToken { get; init; }

    public string Implementation { get; set; } = "";
    public string Review { get; set; } = "";
    public string Changelog { get; set; } = "";
    public int ReviewRounds { get; set; }
    public bool Approved { get; set; }
}

public sealed class PipelineException(string message) : Exception(message);

/// <summary>Asks one agent and stores its answer. Every ask of one run shares a conversation key,
/// so Merino remembers the first implementation when the fix request arrives.</summary>
public class AskAgentNode(string agent, Func<PipelineState, string> prompt, Action<PipelineState, string> store) : AsyncNode
{
    private readonly string _agent = agent;
    private readonly Func<PipelineState, string> _prompt = prompt;
    private readonly Action<PipelineState, string> _store = store;

    protected override Task<object?> PrepAsync(object shared) => Task.FromResult<object?>(shared);

    protected override async Task<object?> ExecAsync(object? prepRes)
    {
        var state = (PipelineState)prepRes!;
        return await AskAsync(state, _agent, _prompt(state));
    }

    protected override Task<object?> PostAsync(object shared, object? prepRes, object? execRes)
    {
        _store((PipelineState)shared, (string)execRes!);
        return Task.FromResult<object?>(null);
    }

    internal static async Task<string> AskAsync(PipelineState state, string agent, string text)
    {
        Console.WriteLine($"\n▶ {agent}");
        var message = new BusMessage(
            Id: Guid.NewGuid().ToString("n")[..12],
            From: "pipeline",
            To: agent,
            Kind: MessageKinds.Ask,
            Text: text,
            ConversationKey: $"pipeline:{state.RunId}",
            TimeoutSeconds: 1800,
            Source: "cli");
        var reply = await state.Bus.SendAsync(message, state.CancellationToken);
        if (!reply.Success)
            throw new PipelineException($"{agent} failed: {reply.Error}");
        Console.WriteLine(reply.Output);
        return reply.Output;
    }
}

/// <summary>Asks Dolly for a review and branches on the verdict she ends her answer with.</summary>
public sealed class ReviewNode(int maxRounds) : AsyncNode
{
    public const string Approved = "approved";
    public const string Changes = "changes";
    public const string GiveUp = "give_up";

    private readonly int _maxRounds = maxRounds;

    protected override Task<object?> PrepAsync(object shared) => Task.FromResult<object?>(shared);

    protected override async Task<object?> ExecAsync(object? prepRes)
    {
        var state = (PipelineState)prepRes!;
        state.ReviewRounds++;
        return await AskAgentNode.AskAsync(state, "dolly", $"""
            Review Merino's change in the shared workspace (round {state.ReviewRounds} of {_maxRounds}).
            The request was: {state.Feature}
            Merino says:
            {state.Implementation}

            Read the changed files and run the tests if you can. List concrete problems only.
            End with exactly one line: VERDICT: APPROVED or VERDICT: CHANGES
            """);
    }

    protected override Task<object?> PostAsync(object shared, object? prepRes, object? execRes)
    {
        var state = (PipelineState)shared;
        var review = (string)execRes!;
        state.Review = review;
        var verdict = review.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("VERDICT:", StringComparison.OrdinalIgnoreCase));
        state.Approved = verdict != null && verdict.Contains("APPROVED", StringComparison.OrdinalIgnoreCase);
        var action = state.Approved ? Approved : state.ReviewRounds >= _maxRounds ? GiveUp : Changes;
        return Task.FromResult<object?>(action);
    }
}

public sealed class ReportNode : AsyncNode
{
    protected override Task<object?> PrepAsync(object shared)
    {
        var state = (PipelineState)shared;
        Console.WriteLine(new string('─', 60));
        Console.WriteLine(state.Approved
            ? $"✔ Approved after {state.ReviewRounds} review round(s)."
            : $"✖ Not approved after {state.ReviewRounds} round(s). Last review:\n{state.Review}");
        if (state.Changelog.Length > 0)
            Console.WriteLine($"\nChangelog:\n{state.Changelog}");
        return Task.FromResult<object?>(null);
    }
}
