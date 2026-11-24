using System.Net;
using System.Text;
using System.Text.Json;

namespace Racka.Tests;

/// <summary>Captures requests and answers them with queued JSON responses.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<string> _responses;

    public FakeHttpHandler(params string[] responses) => _responses = new Queue<string>(responses);

    public List<(HttpRequestMessage Request, JsonElement Body)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? "{}" : await request.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Requests.Add((request, doc.RootElement.Clone()));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json")
        };
    }
}
