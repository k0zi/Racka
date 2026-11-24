using System.ComponentModel;
using System.IO.Pipelines;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Racka.Core.Engine;
using Racka.Core.Mcp;
using Racka.Core.Tools;

namespace Racka.Tests;

public class McpTests
{
    /// <summary>Starts an in-process MCP server connected through pipes and returns a client for it.</summary>
    internal static async Task<(McpClient Client, CancellationTokenSource Stop)> StartServerAsync(params McpServerTool[] tools)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "test-server", Version = "1.0" },
            ToolCollection = new McpServerPrimitiveCollection<McpServerTool>()
        };
        foreach (var tool in tools)
            options.ToolCollection.Add(tool);

        var stop = new CancellationTokenSource();
        var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), "test-server"),
            options);
        _ = server.RunAsync(stop.Token);

        var client = await McpClient.CreateAsync(new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()));
        return (client, stop);
    }

    [Fact]
    public async Task ExposesServerToolsAsRackaTools()
    {
        var (client, stop) = await StartServerAsync(
            McpServerTool.Create([Description("Adds two numbers.")] (int a, int b) => (a + b).ToString(), new() { Name = "add" }),
            McpServerTool.Create(() => { throw new InvalidOperationException("nope"); }, new() { Name = "fail.hard" }));
        using var _ = stop;
        await using var manager = new McpClientManager();
        await manager.AddClientAsync("calc", client, CancellationToken.None);

        var add = manager.Tools.Single(t => t.Name == "calc__add");
        var fail = manager.Tools.Single(t => t.Name == "calc__fail_hard");
        var ctx = new ToolContext(TestAgents.Create(), AgentTask.Create("x", TaskSources.Cli), NullAgentEvents.Instance);

        var sum = await add.InvokeAsync(JsonDocument.Parse("""{"a":2,"b":3}""").RootElement, ctx, CancellationToken.None);
        var error = await fail.InvokeAsync(JsonDocument.Parse("{}").RootElement, ctx, CancellationToken.None);

        Assert.Equal("Adds two numbers.", add.Description);
        Assert.Equal("object", add.InputSchema.GetProperty("type").GetString());
        Assert.False(sum.IsError);
        Assert.Equal("5", sum.Content);
        Assert.True(error.IsError);
    }

    [Fact]
    public void ToolNamesAreSanitizedAndBounded()
    {
        Assert.Equal("git_hub__search.repos".Replace('.', '_'), McpToolAdapter.ToolName("git hub", "search.repos"));
        Assert.Equal(64, McpToolAdapter.ToolName("server", new string('x', 100)).Length);
    }

    [Fact]
    public void FormatsMixedContent()
    {
        var result = new CallToolResult
        {
            Content = [new TextContentBlock { Text = "line" }, new TextContentBlock { Text = "two" }]
        };
        Assert.Equal($"line{Environment.NewLine}two", McpToolAdapter.FormatResult(result));
    }

    [Fact]
    public async Task UnreachableServerIsSkipped()
    {
        await using var manager = new McpClientManager();
        var servers = new Dictionary<string, Racka.Core.Config.McpServerConfig>
        {
            ["broken"] = new() { Command = "/nonexistent/mcp-server" }
        };

        await manager.ConnectAsync(servers, Path.GetTempPath(), CancellationToken.None);

        Assert.Empty(manager.Tools);
    }
}
