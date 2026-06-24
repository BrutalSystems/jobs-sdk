using System.Net;
using System.Text;

namespace BrutalSystems.Jobs.Client.Tests;

/// <summary>Mock HttpMessageHandler: captures each request and returns the next queued response.</summary>
public sealed class StubHandler : HttpMessageHandler
{
    public sealed record Captured(HttpMethod Method, string Url, string Body, string? Auth);

    private readonly Queue<(HttpStatusCode Code, string Json)> _responses = new();
    public List<Captured> Requests { get; } = new();

    public StubHandler Enqueue(HttpStatusCode code, string json = "{}")
    {
        _responses.Enqueue((code, json));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add(new Captured(request.Method, request.RequestUri!.ToString(), body,
            request.Headers.Authorization?.ToString()));
        var (code, json) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, "{}");
        return new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
