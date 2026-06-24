using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BrutalSystems.Jobs.Core;

namespace BrutalSystems.Jobs.Client;

public sealed record TriggerResult(
    [property: System.Text.Json.Serialization.JsonPropertyName("run_id")] string RunId,
    [property: System.Text.Json.Serialization.JsonPropertyName("external_ref")] string ExternalRef,
    [property: System.Text.Json.Serialization.JsonPropertyName("job_id")] string JobId);

/// <summary>HTTP producer client for the jobs service. Port of jobs_client.client.JobsClient.
/// Construct directly in tests; use FromEnv (Task A12) in production.</summary>
public sealed partial class JobsClient
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly Func<string> _tokenProvider;
    private readonly string _baseUrl;

    public JobsClient(HttpClient http, Func<string> tokenProvider, string baseUrl, TimeSpan? timeout = null)
    {
        _http = http;
        _tokenProvider = tokenProvider;
        _baseUrl = baseUrl.TrimEnd('/');
        if (timeout is { } t) _http.Timeout = t;
    }

    private HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, $"{_baseUrl}{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokenProvider());
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        return req;
    }

    private static async Task<T> ReadJson<T>(HttpResponseMessage resp, CancellationToken ct)
        => (await resp.Content.ReadFromJsonAsync<T>(Json, ct))!;

    public async Task<string> RegisterJobAsync(string name, string? schedule, Policy policy,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["schedule"] = schedule,
            ["policy"] = policy,
        };
        using var req = Request(HttpMethod.Post, "/api/v1/jobs", body);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("job_id").GetString()!;
    }

    public async Task<TriggerResult> TriggerAsync(string jobName,
        IReadOnlyList<string>? cmd = null,
        IReadOnlyDictionary<string, object?>? args = null,
        string? runTenant = null, string? runUser = null,
        Func<Task>? onMissRegister = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["args"] = args ?? new Dictionary<string, object?>() };
        if (cmd is not null) body["cmd"] = cmd;
        if (runTenant is not null) body["run_tenant"] = runTenant;
        if (runUser is not null) body["run_user"] = runUser;
        var path = $"/api/v1/jobs/by-name/{jobName}/trigger";

        using var first = Request(HttpMethod.Post, path, body);
        var resp = await _http.SendAsync(first, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound && onMissRegister is not null)
        {
            resp.Dispose();
            await onMissRegister();
            using var retry = Request(HttpMethod.Post, path, body);
            resp = await _http.SendAsync(retry, ct);
        }
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            resp.Dispose();
            throw new JobNotFoundException($"job '{jobName}' not registered");
        }
        using (resp)
        {
            resp.EnsureSuccessStatusCode();
            return await ReadJson<TriggerResult>(resp, ct);
        }
    }
}
