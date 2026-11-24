// Talks to running Racka agents from any .NET application over the local bus.
//
//   dotnet run --project samples/dotnet/ExternalClient -- shaun "Mutatkozz be egy mondatban!"
//
// The bus is one Unix socket per agent in $RACKA_RUNTIME_DIR (default $XDG_RUNTIME_DIR/racka),
// so this works for every engine: Claude Code, Codex, Claude API or a local OpenAI compatible model.
using Racka.Core.Messaging;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var target = args.Length > 0 ? args[0] : "shaun";
var question = args.Length > 1 ? string.Join(' ', args[1..]) : "Mutatkozz be egy mondatban: ki vagy és miben vagy jó?";

using var bus = new BusClient(new BusPaths());

// 1. Discovery: every running agent answers GET /status on its socket.
var agents = await bus.ListAgentsAsync(cts.Token);
if (agents.Count == 0)
{
    Console.Error.WriteLine($"No agents are running in {bus.Paths.RuntimeDirectory}. Start one with: racka run agents/shaun");
    return 1;
}
Console.WriteLine("Running agents:");
foreach (var agent in agents)
    Console.WriteLine($"  {agent.Name,-10} {agent.Engine,-12} {agent.State,-8} queue={agent.QueueLength} done={agent.CompletedTasks}");

if (!bus.Exists(target))
{
    Console.Error.WriteLine($"'{target}' is not running.");
    return 1;
}

// 2. Ask: the request stays open until the agent has finished the task (or the timeout passes).
//    Messages with the same conversation key continue the same conversation on the agent's side.
var ask = new BusMessage(
    Id: Guid.NewGuid().ToString("n")[..12],
    From: "sample:external-client",
    To: target,
    Kind: MessageKinds.Ask,
    Text: question,
    ConversationKey: "sample:external-client",
    TimeoutSeconds: 300,
    Source: "cli");
Console.WriteLine($"\n> {target}: {question}");
var reply = await bus.SendAsync(ask, cts.Token);
Console.WriteLine(reply.Success ? reply.Output : $"error: {reply.Error}");

// 3. Tell: fire and forget. The agent queues the task and works on it after the current one;
//    the result shows up in its log (and on IRC when the agent has a report channel).
var tell = ask with
{
    Id = Guid.NewGuid().ToString("n")[..12],
    Kind = MessageKinds.Tell,
    Text = "Mentsd el a memóriádba, hogy az external-client minta is beszélt veled."
};
var queued = await bus.SendAsync(tell, cts.Token);
Console.WriteLine($"\nqueued: {queued.Queued} ({queued.Id})");

// 4. Status of a single agent.
var status = await bus.GetStatusAsync(target, cts.Token);
Console.WriteLine($"{status.Name} is {status.State}, {status.QueueLength} task(s) waiting.");
return reply.Success ? 0 : 1;
