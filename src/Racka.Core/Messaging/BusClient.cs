using System.Net.Http.Json;
using System.Net.Sockets;

namespace Racka.Core.Messaging;

/// <summary>HTTP/JSON client for agent sockets. The request host name selects the agent socket.</summary>
public sealed class BusClient : IDisposable
{
    private readonly HttpClient _http;

    public BusClient(BusPaths paths)
    {
        Paths = paths;
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(paths.SocketPath(context.DnsEndPoint.Host)), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public BusPaths Paths { get; }

    public bool Exists(string agent) => File.Exists(Paths.SocketPath(agent));

    public async Task<BusReply> SendAsync(BusMessage message, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(Url(message.To, "messages"), message, BusJson.Options, ct);
        return await ReadAsync<BusReply>(response, ct);
    }

    public async Task<AgentStatus> GetStatusAsync(string agent, CancellationToken ct)
    {
        using var response = await _http.GetAsync(Url(agent, "status"), ct);
        return await ReadAsync<AgentStatus>(response, ct);
    }

    public async Task<bool> CancelAsync(string agent, CancellationToken ct)
    {
        using var response = await _http.PostAsync(Url(agent, "cancel"), null, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<ToolDescriptor>> ListToolsAsync(string agent, CancellationToken ct)
    {
        using var response = await _http.GetAsync(Url(agent, "tools"), ct);
        return await ReadAsync<List<ToolDescriptor>>(response, ct);
    }

    public async Task<ToolInvocationResult> InvokeToolAsync(string agent, ToolInvocation invocation, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(Url(agent, "tools/invoke"), invocation, BusJson.Options, ct);
        return await ReadAsync<ToolInvocationResult>(response, ct);
    }

    /// <summary>Status of every agent with a live socket; stale socket files are skipped.</summary>
    public async Task<IReadOnlyList<AgentStatus>> ListAgentsAsync(CancellationToken ct)
    {
        var statuses = new List<AgentStatus>();
        foreach (var name in Paths.ListAgentNames())
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                statuses.Add(await GetStatusAsync(name, timeout.Token));
            }
            catch (Exception e) when (e is HttpRequestException or SocketException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                // stale socket of a stopped agent
            }
        }
        return statuses;
    }

    private static Uri Url(string agent, string path) => new($"http://{agent}/{path}");

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }
        return await response.Content.ReadFromJsonAsync<T>(BusJson.Options, ct)
               ?? throw new HttpRequestException("Empty response.");
    }

    public void Dispose() => _http.Dispose();
}
