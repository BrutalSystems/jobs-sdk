using System.Text.Json;

namespace BrutalSystems.Jobs.Client;

public sealed partial class JobsClient
{
    public async Task<string> StartRunAsync(string jobId, string idempotencyKey,
        string trigger = "schedule", string? runId = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["trigger"] = trigger, ["idempotency_key"] = idempotencyKey };
        if (runId is not null) body["run_id"] = runId;
        using var req = Request(HttpMethod.Post, $"/api/v1/jobs/{jobId}/runs", body);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("run_id").GetString()!;
    }

    public async Task<string> FinishRunAsync(string jobId, string runId, string status,
        int? exitCode, IReadOnlyDictionary<string, object?>? summary, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["status"] = status, ["exit_code"] = exitCode, ["summary"] = summary,
        };
        using var req = Request(HttpMethod.Patch, $"/api/v1/jobs/{jobId}/runs/{runId}", body);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("status").GetString()!;
    }

    /// <summary>Best-effort log forward. Never throws — log failures must not fail handlers.</summary>
    public async Task LogAsync(string jobId, string runId, string line, CancellationToken ct = default)
    {
        try
        {
            using var req = Request(HttpMethod.Post, $"/api/v1/jobs/{jobId}/runs/{runId}/logs",
                new Dictionary<string, object?> { ["line"] = line });
            using var resp = await _http.SendAsync(req, ct);
        }
        catch
        {
            // swallowed by contract
        }
    }

    public async Task HeartbeatAsync(string jobId, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Post, $"/api/v1/jobs/{jobId}/heartbeat");
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }
}
