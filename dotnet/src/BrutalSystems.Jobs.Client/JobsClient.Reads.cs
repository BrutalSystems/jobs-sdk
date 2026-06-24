using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BrutalSystems.Jobs.Core;

namespace BrutalSystems.Jobs.Client;

public sealed partial class JobsClient
{
    public async Task<JsonElement> GetJobByNameAsync(string ownerService, string name, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Get, $"/api/v1/jobs/by-name/{name}");
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new JobNotFoundException($"job '{name}' not registered for owner '{ownerService}'");
        resp.EnsureSuccessStatusCode();
        return await ReadJson<JsonElement>(resp, ct);
    }

    public async Task<IReadOnlyList<JsonElement>> ListJobsFilteredAsync(
        string? ownerService = null, string? dispatchMode = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (ownerService is not null) query.Add($"owner_service={Uri.EscapeDataString(ownerService)}");
        if (dispatchMode is not null) query.Add($"dispatch_mode={Uri.EscapeDataString(dispatchMode)}");
        var path = "/api/v1/jobs" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        using var req = Request(HttpMethod.Get, path);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await ReadJson<List<JsonElement>>(resp, ct);
    }

    public async Task<JsonElement> GetRunByExternalRefAsync(string externalRef, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Get, $"/api/v1/runs/by-external-ref/{externalRef}");
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new RunNotFoundException($"no run with external_ref={externalRef}");
        resp.EnsureSuccessStatusCode();
        return await ReadJson<JsonElement>(resp, ct);
    }

    /// <summary>Construct from the environment (port of JobsClient.from_env). owner_service has no
    /// app default (it would re-bake an identity into the shared SDK); tenant falls back to "_org".
    /// The signing key comes from the arg, else the JOBS_JWT_PRIVATE_KEY PEM.</summary>
    public static JobsClient FromEnv(HttpClient http, string? ownerService = null,
        string? tenantId = null, RSA? signingKey = null)
    {
        var baseUrl = Environment.GetEnvironmentVariable(PodEnv.ServiceUrl)
            ?? throw new InvalidOperationException($"{PodEnv.ServiceUrl} env var required");
        var owner = ownerService ?? Environment.GetEnvironmentVariable(PodEnv.OwnerService)
            ?? throw new InvalidOperationException($"owner_service required: pass it or set {PodEnv.OwnerService}");
        var tenant = tenantId ?? Environment.GetEnvironmentVariable(PodEnv.TenantId) ?? "_org";

        var key = signingKey;
        if (key is null)
        {
            var pem = Environment.GetEnvironmentVariable(PodEnv.JwtPrivateKey)
                ?? throw new InvalidOperationException($"{PodEnv.JwtPrivateKey} env var required for m2m token minting");
            key = RSA.Create();
            key.ImportFromPem(pem);
        }
        var provider = new TokenProvider(new M2mTokenMinter(key, owner, tenant));
        return new JobsClient(http, provider.GetToken, baseUrl);
    }
}
