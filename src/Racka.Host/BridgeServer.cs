using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Racka.Core.Messaging;

namespace Racka.Host;

/// <summary>
/// `racka bridge`: a stdio MCP server started by Claude Code. It exposes the agent's built-in
/// Racka tools (send_message, list_agents, chat_report) and forwards calls to the agent socket.
/// </summary>
internal static class BridgeServer
{
    public static async Task<int> RunAsync(string agentName, BusPaths paths, CancellationToken ct)
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace));
        using var bus = new BusClient(paths);
        var tools = await bus.ListToolsAsync(agentName, ct);
        var taskId = Environment.GetEnvironmentVariable("RACKA_TASK_ID");

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "racka", Version = "1.0" },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult
                {
                    Tools = tools.Select(t => new Tool { Name = t.Name, Description = t.Description, InputSchema = t.InputSchema }).ToList()
                }),
                CallToolHandler = async (request, token) =>
                {
                    var name = request.Params?.Name ?? "";
                    var args = request.Params?.Arguments is { } a
                        ? JsonSerializer.SerializeToElement(a)
                        : JsonDocument.Parse("{}").RootElement.Clone();
                    var result = await bus.InvokeToolAsync(agentName, new ToolInvocation(name, args, taskId), token);
                    return new CallToolResult
                    {
                        Content = [new TextContentBlock { Text = result.Content }],
                        IsError = result.IsError
                    };
                }
            }
        };

        await using var server = McpServer.Create(new StdioServerTransport("racka", loggerFactory), options, loggerFactory);
        await server.RunAsync(ct);
        return 0;
    }
}
