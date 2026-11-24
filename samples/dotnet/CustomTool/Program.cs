// Hosts an agent with your own C# tools. The tools work with every engine: the API engines call them
// directly, Claude Code and Codex reach them through the bridge MCP server.
//
//   cd samples/dotnet/CustomTool && dotnet run            # runs ./agent (the "shepherd" agent)
//   racka send shepherd "Nyiss egy ticketet Merinónak: a wordcount.py ne számolja a sorvégi szóközt." --wait
using Racka.Core.Agents;
using Racka.Core.Messaging;
using Racka.Host;
using Racka.Providers;
using Racka.Samples.CustomTool;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var agent = AgentDefinition.Load(args.Length > 0 ? args[0] : "agent");
var board = new TicketBoard(Path.Combine(agent.StateDirectory, "tickets.json"));

return await AgentHost.RunAsync(agent, new BusPaths(), AgentEngineFactory.Create, cts.Token,
    extraTools: [new TicketCreateTool(board), new TicketListTool(board), new TicketCloseTool(board)]);
