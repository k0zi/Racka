using System.CommandLine;
using System.Net.Sockets;
using System.Text.Json;
using Racka.Core.Config;
using Racka.Core.Messaging;
using Racka.Host;
using YamlDotNet.RepresentationModel;

// dotnet tool install runs no code, so the user settings are created on the first run.
if (args is not ["bridge", ..])
    Cli.CreateUserSettings();

var runtimeDirOption = new Option<string?>("--runtime-dir")
{
    Description = "Directory of the agent sockets (default: $RACKA_RUNTIME_DIR, runtimeDir in the user settings, or $XDG_RUNTIME_DIR/racka).",
    Recursive = true
};
var jsonOption = new Option<bool>("--json") { Description = "Print machine readable JSON." };

var root = new RootCommand("Racka – a multi-agent framework on HerdFlow.");
root.Options.Add(runtimeDirOption);

// racka run <agent-dir>
var agentDirArgument = new Argument<DirectoryInfo>("agent-dir") { Description = "Agent directory containing agent.yaml and soul.md." };
var ircServerOption = new Option<string?>("--irc-server") { Description = "IRC server to connect to (overrides irc.server)." };
var ircPortOption = new Option<int?>("--irc-port") { Description = "IRC port (overrides irc.port)." };
var ircTlsOption = new Option<bool?>("--irc-tls") { Description = "true or false: connect with TLS (overrides irc.tls)." };
var ircChannelOption = new Option<string[]>("--irc-channel") { Description = "IRC channel to join; repeat for more (overrides irc.channels)." };
var ircNickOption = new Option<string?>("--irc-nick") { Description = "IRC nick (overrides irc.nick)." };
var noChatOption = new Option<bool>("--no-chat") { Description = "Do not connect to any chat platform." };
var run = new Command("run", "Run an agent in the foreground.") { agentDirArgument, ircServerOption, ircPortOption, ircTlsOption, ircChannelOption, ircNickOption, noChatOption };
run.SetAction((parse, ct) => Cli.Guard(() =>
{
    var overrides = new YamlMappingNode();
    var irc = new YamlMappingNode();
    if (parse.GetValue(ircServerOption) is { } server)
        irc.Add("server", server);
    if (parse.GetValue(ircPortOption) is { } port)
        irc.Add("port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
    if (parse.GetValue(ircTlsOption) is { } tls)
        irc.Add("tls", tls ? "true" : "false");
    if (parse.GetValue(ircChannelOption) is { Length: > 0 } channels)
        irc.Add("channels", new YamlSequenceNode(channels.Select(c => new YamlScalarNode(c))));
    if (parse.GetValue(ircNickOption) is { } nick)
        irc.Add("nick", nick);
    if (irc.Children.Count > 0)
        overrides.Add("irc", irc);
    if (parse.GetValue(noChatOption))
    {
        foreach (var section in UserSettings.ChatSections)
            overrides.Children[new YamlScalarNode(section)] = new YamlMappingNode { { "enabled", "false" } };
    }
    return AgentHost.RunAsync(parse.GetValue(agentDirArgument)!.FullName, Paths(parse), ct, overrides);
}));
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
    Console.WriteLine($"{"NAME",-16} {"ENGINE",-12} {"MODEL",-20} {"STATE",-6} {"QUEUE",5} {"DONE",5} {"FAIL",5} {"CHAT",-14} PID");
    foreach (var a in agents)
    {
        var chat = a.Chat ?? (a.IrcConnected ? ["irc"] : []);
        Console.WriteLine($"{a.Name,-16} {a.Engine,-12} {a.Model ?? "-",-20} {a.State,-6} {a.QueueLength,5} {a.CompletedTasks,5} {a.FailedTasks,5} {(chat.Count == 0 ? "-" : string.Join(',', chat)),-14} {a.ProcessId}");
    }
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

// racka config …
var configAgentOption = new Option<DirectoryInfo?>("--agent", "-a") { Description = "Edit this agent's agent.yaml instead of the user settings.", Recursive = true };
var configKey = new Argument<string>("key") { Description = "Dotted key, e.g. irc.server or telegram.operators." };
var configValue = new Argument<string>("value") { Description = "New value; lists take comma separated items (\"#a,#b\")." };
var forceOption = new Option<bool>("--force") { Description = "Overwrite an existing file." };
var configCommand = new Command("config", "Show or change the user settings (~/.config/racka/config.yaml) or an agent's agent.yaml.") { configAgentOption };

var configPath = new Command("path", "Print the path of the settings file.");
configPath.SetAction(parse => Cli.Run(() =>
{
    Console.WriteLine(OpenConfig(parse).Path);
    return 0;
}));
var configShow = new Command("show", "Print the settings file.");
configShow.SetAction(parse => Cli.Run(() =>
{
    var path = OpenConfig(parse).Path;
    if (!File.Exists(path))
        return Cli.Fail($"{path} does not exist yet; create it with racka config init.");
    Console.Write(File.ReadAllText(path));
    return 0;
}));
var configInit = new Command("init", "Write the commented user settings template.") { forceOption };
configInit.SetAction(parse => Cli.Run(() =>
{
    var path = RackaDirectories.ConfigFile;
    if (parse.GetValue(forceOption) && File.Exists(path))
        File.Delete(path);
    Console.WriteLine(UserSettings.EnsureCreated(path) ? $"Created {path}" : $"{path} already exists (use --force to overwrite it).");
    return 0;
}));
var configGet = new Command("get", "Print one value.") { configKey };
configGet.SetAction(parse => Cli.Run(() =>
{
    var value = OpenConfig(parse).Get(parse.GetValue(configKey)!);
    if (value == null)
        return 1;
    Console.WriteLine(value);
    return 0;
}));
var configSet = new Command("set", "Set one value (rewrites the file without its comments).") { configKey, configValue };
configSet.SetAction(parse => Cli.Run(() =>
{
    var editor = OpenConfig(parse);
    editor.Set(parse.GetValue(configKey)!, parse.GetValue(configValue)!);
    editor.Save();
    Console.WriteLine($"{parse.GetValue(configKey)} set in {editor.Path}");
    return 0;
}));
var configUnset = new Command("unset", "Remove one value.") { configKey };
configUnset.SetAction(parse => Cli.Run(() =>
{
    var editor = OpenConfig(parse);
    if (!editor.Unset(parse.GetValue(configKey)!))
        return Cli.Fail($"{parse.GetValue(configKey)} is not set in {editor.Path}.");
    editor.Save();
    return 0;
}));
configCommand.Subcommands.Add(configPath);
configCommand.Subcommands.Add(configShow);
configCommand.Subcommands.Add(configInit);
configCommand.Subcommands.Add(configGet);
configCommand.Subcommands.Add(configSet);
configCommand.Subcommands.Add(configUnset);
root.Subcommands.Add(configCommand);

// racka bridge --agent <name> (started by Claude Code)
var bridgeAgent = new Option<string>("--agent") { Description = "Agent whose tools to expose.", Required = true };
var bridge = new Command("bridge", "Internal: MCP stdio bridge that Claude Code and Codex agents use to reach Racka tools.") { bridgeAgent };
bridge.Hidden = true;
bridge.SetAction((parse, ct) => BridgeServer.RunAsync(parse.GetValue(bridgeAgent)!, Paths(parse), ct));
root.Subcommands.Add(bridge);

return await root.Parse(args).InvokeAsync();

BusPaths Paths(ParseResult parse) => BusPaths.Resolve(parse.GetValue(runtimeDirOption), UserSettings.Load().RuntimeDir);

ConfigEditor OpenConfig(ParseResult parse) =>
    parse.GetValue(configAgentOption) is { } agentDir ? ConfigEditor.OpenAgent(agentDir.FullName) : ConfigEditor.OpenUserSettings();

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

    public static int Run(Func<int> action) => Guard(() => Task.FromResult(action())).GetAwaiter().GetResult();

    public static void CreateUserSettings()
    {
        try
        {
            if (UserSettings.EnsureCreated())
                Console.Error.WriteLine($"racka: created the user settings in {RackaDirectories.ConfigFile} (racka config --help)");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"racka: could not create {RackaDirectories.ConfigFile}: {e.Message}");
        }
    }

    public static int Fail(string message)
    {
        Console.Error.WriteLine($"racka: {message}");
        return 1;
    }
}
