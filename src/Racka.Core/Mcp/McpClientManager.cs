using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Racka.Core.Config;
using Racka.Core.Tools;

namespace Racka.Core.Mcp;

/// <summary>Connects to an agent's MCP servers and exposes their tools as <see cref="IRackaTool"/>s.</summary>
public sealed class McpClientManager : IAsyncDisposable
{
    private readonly List<McpClient> _clients = new();
    private readonly List<IRackaTool> _tools = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    public McpClientManager(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<McpClientManager>();
    }

    public IReadOnlyList<IRackaTool> Tools => _tools;

    /// <summary>Connects to every server; a server that fails to start is logged and skipped.</summary>
    public async Task ConnectAsync(IReadOnlyDictionary<string, McpServerConfig> servers, string workingDirectory, CancellationToken ct)
    {
        foreach (var (name, config) in servers.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            try
            {
                var client = await McpClient.CreateAsync(CreateTransport(name, config, workingDirectory), loggerFactory: _loggerFactory, cancellationToken: ct);
                _clients.Add(client);
                var tools = await client.ListToolsAsync(cancellationToken: ct);
                foreach (var tool in tools)
                    _tools.Add(new McpToolAdapter(name, tool));
                _logger.LogInformation("MCP server {Server}: {Count} tools", name, tools.Count);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(e, "MCP server {Server} could not be started", name);
            }
        }
    }

    /// <summary>Adds tools from an already connected client (used by tests and in-process servers).</summary>
    public async Task AddClientAsync(string serverName, McpClient client, CancellationToken ct)
    {
        _clients.Add(client);
        foreach (var tool in await client.ListToolsAsync(cancellationToken: ct))
            _tools.Add(new McpToolAdapter(serverName, tool));
    }

    internal static IClientTransport CreateTransport(string name, McpServerConfig config, string workingDirectory)
    {
        if (config.IsStdio)
        {
            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = name,
                Command = config.Command!,
                Arguments = config.Args,
                WorkingDirectory = workingDirectory,
                EnvironmentVariables = config.Env.ToDictionary(kv => kv.Key, kv => (string?)kv.Value)
            });
        }
        return new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = name,
            Endpoint = new Uri(config.Url!),
            AdditionalHeaders = config.Headers
        });
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            try { await client.DisposeAsync(); }
            catch (Exception e) { _logger.LogDebug(e, "Error while closing MCP client"); }
        }
        _clients.Clear();
    }
}

/// <summary>An MCP tool exposed as a Racka tool named &lt;server&gt;__&lt;tool&gt;.</summary>
public sealed partial class McpToolAdapter : IRackaTool
{
    private const int MaxResultChars = 100_000;
    private readonly McpClientTool _tool;

    public McpToolAdapter(string serverName, McpClientTool tool)
    {
        _tool = tool;
        Name = ToolName(serverName, tool.Name);
        Description = string.IsNullOrWhiteSpace(tool.Description) ? $"{tool.Name} (MCP server {serverName})" : tool.Description;
        InputSchema = tool.JsonSchema.ValueKind == JsonValueKind.Object
            ? tool.JsonSchema
            : JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();
    }

    public string Name { get; }
    public string Description { get; }
    public JsonElement InputSchema { get; }

    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex InvalidNameChars();

    /// <summary>Tool names must match ^[a-zA-Z0-9_-]{1,64}$ for the model APIs.</summary>
    public static string ToolName(string server, string tool)
    {
        var name = InvalidNameChars().Replace($"{server}__{tool}", "_");
        return name.Length <= 64 ? name : name[..64];
    }

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        var args = new Dictionary<string, object?>();
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in arguments.EnumerateObject())
                args[p.Name] = p.Value.Clone();
        }
        var result = await _tool.CallAsync(args, cancellationToken: ct);
        return new ToolResult(FormatResult(result), result.IsError == true);
    }

    internal static string FormatResult(CallToolResult result)
    {
        var sb = new StringBuilder();
        foreach (var block in result.Content)
        {
            if (sb.Length > 0)
                sb.AppendLine();
            switch (block)
            {
                case TextContentBlock text:
                    sb.Append(text.Text);
                    break;
                case ImageContentBlock image:
                    sb.Append($"[image {image.MimeType}, {image.Data.Length} bytes base64]");
                    break;
                case EmbeddedResourceBlock { Resource: TextResourceContents resource }:
                    sb.Append($"[resource {resource.Uri}]\n{resource.Text}");
                    break;
                default:
                    sb.Append($"[{block.Type} content]");
                    break;
            }
        }
        if (sb.Length == 0 && result.StructuredContent is { } structured)
            sb.Append(structured.ToString());
        var formatted = sb.ToString();
        return formatted.Length <= MaxResultChars ? formatted : formatted[..MaxResultChars] + "\n…(truncated)";
    }
}
