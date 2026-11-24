using System.Reflection;
using Racka.Chat;
using Racka.Core.Agents;
using Racka.Core.Config;
using Racka.Core.Engine;
using Racka.Core.Mcp;
using Racka.Core.Memory;
using Racka.Core.Messaging;
using Racka.Core.Runtime;
using Racka.Core.Skills;
using Racka.Core.Tools;
using Racka.Providers;
using Racka.Providers.ClaudeCode;
using YamlDotNet.RepresentationModel;

namespace Racka.Host;

public delegate IAgentEngine EngineFactory(AgentDefinition agent, IReadOnlyList<IRackaTool> tools, IAgentEvents events,
    PromptOptions promptOptions, BridgeCommand? bridge);

/// <summary>Composes and runs one agent process: engine, inbox, bus socket and chat connections.</summary>
public static class AgentHost
{
    /// <param name="overrides">Chat sections laid over agent.yaml, e.g. from `racka run --irc-server`.</param>
    public static Task<int> RunAsync(string agentDirectory, BusPaths paths, CancellationToken ct, YamlMappingNode? overrides = null) =>
        RunAsync(AgentDefinition.Load(agentDirectory, layers: ConfigLayers.From(UserSettings.Load(), overrides)), paths, AgentEngineFactory.Create, ct);

    /// <param name="extraTools">Custom tools for this agent. Every engine can call them; the CLI engines through the bridge.</param>
    public static async Task<int> RunAsync(AgentDefinition agent, BusPaths paths, EngineFactory engineFactory, CancellationToken ct,
        IEnumerable<IRackaTool>? extraTools = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);
        var socketPath = paths.SocketPath(agent.Name);
        builder.WebHost.ConfigureKestrel(o => o.ListenUnixSocket(socketPath));
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = BusJson.Options.PropertyNamingPolicy;
            o.SerializerOptions.PropertyNameCaseInsensitive = true;
        });
        await using var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("racka." + agent.Name);

        using var bus = new BusClient(paths);
        paths.EnsureRuntimeDirectory();
        await ClaimSocketAsync(bus, agent.Name, socketPath, ct);

        var events = new AgentEventHub();
        using var logSubscription = events.Subscribe(e => logger.LogInformation("[{Type}] {Task} {Message}", e.Type, e.TaskId, e.Message));

        // Tools every engine can use; the CLI engines reach them through the bridge.
        var bridgedTools = new List<IRackaTool> { new SendMessageTool(bus, agent.Name), new ListAgentsTool(bus, agent.Name) };
        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
        var connectors = ChatConnectors.Create(agent, loggerFactory);
        await using var chat = connectors.Count > 0 ? new ChatService(connectors, events, loggerFactory.CreateLogger($"racka.{agent.Name}.chat")) : null;
        if (chat != null)
            bridgedTools.Add(new ChatReportTool(chat));
        if (agent.Memory != null)
            bridgedTools.AddRange(MemoryTools.Create(agent.Memory));
        // Claude Code discovers skills natively; every other engine loads them with these tools.
        if (agent.Config.EngineKind != EngineKind.ClaudeCode && agent.Skills.All.Count > 0)
            bridgedTools.AddRange([new LoadSkillTool(agent.Skills), new ReadSkillFileTool(agent.Skills)]);
        bridgedTools.AddRange(extraTools ?? []);

        await using var mcp = new McpClientManager(loggerFactory);
        var engineTools = new List<IRackaTool>(bridgedTools);
        if (!agent.Config.EngineKind.IsCli())
        {
            await mcp.ConnectAsync(agent.Config.McpServers, agent.WorkDirectory, ct);
            engineTools.AddRange(mcp.Tools);
        }
        Directory.CreateDirectory(agent.WorkDirectory);

        var promptOptions = new PromptOptions(IncludeSkillIndex: true, CanMessageAgents: true, ChatPlatforms: chat?.Platforms);
        await using var engine = engineFactory(agent, engineTools, events, promptOptions, ResolveBridge(agent.Name, paths));
        var runtime = new AgentRuntime(agent, engine, events);
        if (chat != null)
        {
            runtime.ChatConnected = () => chat.ConnectedPlatforms;
            chat.Attach(runtime);
        }

        BusEndpoints.Map(app, runtime, bridgedTools);

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, app.Lifetime.ApplicationStopping);
        await app.StartAsync(ct);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        logger.LogInformation("Agent {Agent} ({Engine}) listening on {Socket}", agent.Name, agent.Config.Engine, socketPath);

        var chatTask = chat?.RunAsync(stopping.Token) ?? Task.CompletedTask;
        try
        {
            await runtime.RunAsync(stopping.Token);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            await chatTask.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
            TryDelete(socketPath);
        }
        return 0;
    }

    /// <summary>Refuses to start when the agent already runs; removes a stale socket file otherwise.</summary>
    private static async Task ClaimSocketAsync(BusClient bus, string agentName, string socketPath, CancellationToken ct)
    {
        if (!File.Exists(socketPath))
            return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var status = await bus.GetStatusAsync(agentName, timeout.Token);
            throw new ConfigException($"Agent '{agentName}' is already running (pid {status.ProcessId}).");
        }
        catch (Exception e) when (e is not ConfigException)
        {
            TryDelete(socketPath);
        }
    }

    /// <summary>
    /// The command a CLI engine runs to start the bridge. The racka CLI runs itself; an application that embeds
    /// AgentHost uses $RACKA_EXECUTABLE, the racka CLI copied next to its referenced racka.dll, or `racka` from PATH.
    /// </summary>
    internal static BridgeCommand ResolveBridge(string agentName, BusPaths paths)
    {
        List<string> args = ["bridge", "--agent", agentName, "--runtime-dir", paths.RuntimeDirectory];
        var rackaAssembly = typeof(AgentHost).Assembly;
        var processPath = Environment.ProcessPath ?? "racka";
        var startedViaDotnet = Path.GetFileNameWithoutExtension(processPath) == "dotnet";

        if (Assembly.GetEntryAssembly() == rackaAssembly)
            return startedViaDotnet ? new BridgeCommand(processPath, [rackaAssembly.Location, .. args]) : new BridgeCommand(processPath, args);

        if (Environment.GetEnvironmentVariable("RACKA_EXECUTABLE") is { Length: > 0 } configured)
            return new BridgeCommand(configured, args);
        var rackaDll = rackaAssembly.Location;
        var appHost = Path.Combine(Path.GetDirectoryName(rackaDll) ?? "", OperatingSystem.IsWindows() ? "racka.exe" : "racka");
        if (rackaDll.Length > 0 && File.Exists(appHost))
            return new BridgeCommand(appHost, args);
        if (rackaDll.Length > 0 && File.Exists(Path.ChangeExtension(rackaDll, ".runtimeconfig.json")))
            return new BridgeCommand(startedViaDotnet ? processPath : "dotnet", [rackaDll, .. args]);
        return new BridgeCommand("racka", args);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}
