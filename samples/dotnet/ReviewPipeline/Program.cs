// A deterministic HerdFlow pipeline over three agents running on three different engines:
//
//   Implement (merino, Codex) → Review (dolly, Claude Code) ─changes→ Fix (merino) → Review …
//                                                          ─approved→ Changelog (lamb, local model) → Report
//                                                          ─give_up→ Report
//
//   racka run agents/merino & racka run agents/dolly & racka run agents/lamb &
//   dotnet run --project samples/dotnet/ReviewPipeline -- "Write wordcount.py that counts words per line of stdin, with pytest tests."
//
// Shaun can run the same collaboration on his own with send_message; this sample shows how to pin the
// workflow down in code when you want the same steps every time.
using HerdFlow;
using Racka.Core.Messaging;
using Racka.Samples.ReviewPipeline;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var feature = args.Length > 0
    ? string.Join(' ', args)
    : "Write wordcount.py: read stdin and print the number of words on each line. Add pytest tests in test_wordcount.py.";

using var bus = new BusClient(new BusPaths());
foreach (var agent in new[] { "merino", "dolly", "lamb" })
{
    if (!bus.Exists(agent))
    {
        Console.Error.WriteLine($"'{agent}' is not running. Start the herd first: samples/scripts/herd.sh start");
        return 1;
    }
}

var state = new PipelineState
{
    Feature = feature,
    Bus = bus,
    RunId = Guid.NewGuid().ToString("n")[..8],
    CancellationToken = cts.Token
};

var implement = new AskAgentNode("merino",
    s => $"""
        Implement this in your workspace:
        {s.Feature}

        Work alone: this pipeline arranges the review, so do not message other agents.
        Reply with the files you changed and the exact command that runs the tests.
        """,
    (s, reply) => s.Implementation = reply);

var review = new ReviewNode(maxRounds: 3);

var fix = new AskAgentNode("merino",
    s => $"""
        Dolly reviewed your change and asks for these fixes:
        {s.Review}

        Apply them, run the tests again and reply with what you changed.
        """,
    (s, reply) => s.Implementation = reply);

var changelog = new AskAgentNode("lamb",
    s => $"""
        Write a changelog entry for this change, following your changelog skill.

        Request: {s.Feature}
        Implementation notes from Merino:
        {s.Implementation}
        Review from Dolly:
        {s.Review}
        """,
    (s, reply) => s.Changelog = reply);

var report = new ReportNode();

implement.Then(review);
review.On(ReviewNode.Changes).Then(fix);
fix.Then(review);
review.On(ReviewNode.Approved).Then(changelog);
review.On(ReviewNode.GiveUp).Then(report);
changelog.Then(report);

try
{
    await new AsyncFlow(implement).RunAsync(state);
    return state.Approved ? 0 : 2;
}
catch (PipelineException e)
{
    Console.Error.WriteLine($"\n✖ {e.Message}");
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("\ncancelled");
    return 130;
}
