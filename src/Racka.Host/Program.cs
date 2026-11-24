using System.CommandLine;
using System.Net.Sockets;
using System.Text.Json;
using Racka.Core.Config;
using Racka.Core.Messaging;
using Racka.Host;

var runtimeDirOption = new Option<string?>("--runtime-dir")
{
    Description = "Directory of the agent sockets (default: $RACKA_RUNTIME_DIR or $XDG_RUNTIME_DIR/racka).",
    Recursive = true
};
var jsonOption = new Option<bool>("--json") { Description = "Print machine readable JSON." };

var root = new RootCommand("Racka – a multi-agent framework on HerdFlow.");
root.Options.Add(runtimeDirOption);

// racka run <agent-dir>
var agentDirArgument = new Argument<DirectoryInfo>("agent-dir") { Description = "Agent directory containing agent.yaml and soul.md." };
var run = new Command("run", "Run an agent in the foreground.") { agentDirArgument };
run.SetAction((parse, ct) => Cli.Guard(() => AgentHost.RunAsync(parse.GetValue(agentDirArgument)!.FullName, Paths(parse), ct)));
root.Subcommands.Add(run);

// racka send <agent> <text...>
var targetArgument = new Argument<string>("agent") { Description = "Target agent name." };
var textArgument = new Argument<string[]>("text") { Description = "Message text; '-' reads it from stdin.", Arity = ArgumentArity.OneOrMore };
var waitOption = new Option<bool>("--wait", "-w") { Description = "Wait for the agent's answer and print it." };
var timeoutOption = new Option<int>("--timeout") { Description = "Seconds to wait with --wait.", DefaultValueFactory = _ => 600 };
var fromOption = new Option<string?>("--from") { Description = "Sender name (default cli:$USER)." };
var conversationOption = new Option<string?>("--conversation", "-c") { Description = "Conversation key; tasks with the same key share history." };
var send = new Command("send", "Send a task to a running agent.")
{
    targetArgument, textArgument, waitOption, timeoutOption, fromOption, conversationOption, jsonOption
};
send.SetAction((parse, ct) => Cli.Guard(async () =>
{
    var words = parse.GetValue(textArgument)!;
    var text = words is ["-"] ? await Console.In.ReadToEndAsync(ct) : string.Join(' ', words);
    var from = parse.GetValue(fromOption) ?? $"cli:{Environment.UserName}";
    var wait = parse.GetValue(waitOption);
    var timeout = parse.GetValue(timeoutOption);
    var message = new BusMessage(Guid.NewGuid().ToString("n")[..12], from, parse.GetValue(targetArgument)!,
        wait ? MessageKinds.Ask : MessageKinds.Tell, text, parse.GetValue(conversationOption), timeout, Source: "cli");

    using var bus = new BusClient(Paths(parse));
    if (!bus.Exists(message.To))
        return Cli.Fail($"Agent '{message.To}' is not running.");
    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout + 10));
    var reply = await bus.SendAsync(message, timeoutCts.Token);

    if (parse.GetValue(jsonOption))
        Console.WriteLine(JsonSerializer.Serialize(reply, BusJson.Options));
    else if (reply.Queued)
        Console.WriteLine($"queued {reply.Id}");
    else if (reply.Success)
        Console.WriteLine(reply.Output);
    else
        Console.Error.WriteLine($"error: {reply.Error}{(string.IsNullOrEmpty(reply.Output) ? "" : "\n" + reply.Output)}");
    return reply.Success ? 0 : 1;
}));
root.Subcommands.Add(send);

// racka list
var list = new Command("list", "List running agents.") { jsonOption };
list.SetAction((parse, ct) => Cli.Guard(async () =>
{
    using var bus = new BusClient(Paths(parse));
    var agents = await bus.ListAgentsAsync(ct);
    if (parse.GetValue(jsonOption))
    {
        Console.WriteLine(JsonSerializer.Serialize(agents, BusJson.Options));
        return 0;
    }
    if (agents.Count == 0)
    {
        Console.WriteLine("No agents are running.");
        return 0;
    }
    Console.WriteLine($"{"NAME",-16} {"ENGINE",-12} {"MODEL",-20} {"STATE",-6} {"QUEUE",5} {"DONE",5} {"FAIL",5} {"IRC",-4} PID");
    foreach (var a in agents)
        Console.WriteLine($"{a.Name,-16} {a.Engine,-12} {a.Model ?? "-",-20} {a.State,-6} {a.QueueLength,5} {a.CompletedTasks,5} {a.FailedTasks,5} {(a.IrcConnected ? "yes" : "-"),-4} {a.ProcessId}");
    return 0;
}));
root.Subcommands.Add(list);

// racka status <agent>
var statusAgent = new Argument<string>("agent") { Description = "Agent name." };
var status = new Command("status", "Show an agent's status as JSON.") { statusAgent };
status.SetAction((parse, ct) => Cli.Guard(async () =>
{
    using var bus = new BusClient(Paths(parse));
    var s = await bus.GetStatusAsync(parse.GetValue(statusAgent)!, ct);
    Console.WriteLine(JsonSerializer.Serialize(s, new JsonSerializerOptions(BusJson.Options) { WriteIndented = true }));
    return 0;
}));
root.Subcommands.Add(status);

// racka cancel <agent>
var cancelAgent = new Argument<string>("agent") { Description = "Agent name." };
var cancel = new Command("cancel", "Cancel the task an agent is running.") { cancelAgent };
cancel.SetAction((parse, ct) => Cli.Guard(async () =>
{
    using var bus = new BusClient(Paths(parse));
    var cancelled = await bus.CancelAsync(parse.GetValue(cancelAgent)!, ct);
    Console.WriteLine(cancelled ? "cancelled" : "agent is idle");
    return 0;
}));
root.Subcommands.Add(cancel);

// racka new <name>
var newName = new Argument<string>("name") { Description = "Agent name (lowercase)." };
var engineOption = new Option<string>("--engine", "-e") { Description = "claude-code, codex, anthropic or openai.", DefaultValueFactory = _ => "anthropic" };
var dirOption = new Option<string>("--dir") { Description = "Parent directory of the agents.", DefaultValueFactory = _ => "agents" };
var create = new Command("new", "Create a new agent from a template.") { newName, engineOption, dirOption };
create.SetAction(parse => Cli.Guard(() =>
{
    var dir = Scaffolder.Create(parse.GetValue(dirOption)!, parse.GetValue(newName)!, parse.GetValue(engineOption)!);
    Console.WriteLine($"Created {dir}. Edit soul.md and agent.yaml, then: racka run {Path.GetRelativePath(Environment.CurrentDirectory, dir)}");
    return Task.FromResult(0);
}).GetAwaiter().GetResult());
root.Subcommands.Add(create);

// racka bridge --agent <name> (started by Claude Code)
var bridgeAgent = new Option<string>("--agent") { Description = "Agent whose tools to expose.", Required = true };
var bridge = new Command("bridge", "Internal: MCP stdio bridge that Claude Code and Codex agents use to reach Racka tools.") { bridgeAgent };
bridge.Hidden = true;
bridge.SetAction((parse, ct) => BridgeServer.RunAsync(parse.GetValue(bridgeAgent)!, Paths(parse), ct));
root.Subcommands.Add(bridge);

return await root.Parse(args).InvokeAsync();

BusPaths Paths(ParseResult parse) => new(parse.GetValue(runtimeDirOption));

internal static class Cli
{
    public static async Task<int> Guard(Func<Task<int>> action)
    {
        try
        {
            return await action();
        }
        catch (ConfigException e)
        {
            return Fail(e.Message);
        }
        catch (Exception e) when (e is HttpRequestException or SocketException)
        {
            return Fail($"Could not reach the agent: {e.Message}");
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
    }

    public static int Fail(string message)
    {
        Console.Error.WriteLine($"racka: {message}");
        return 1;
    }
}
