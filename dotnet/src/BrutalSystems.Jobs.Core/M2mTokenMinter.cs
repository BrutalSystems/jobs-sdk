using System.Security.Cryptography;

namespace BrutalSystems.Jobs.Core;

/// <summary>Mints m2m JWTs for talking to the jobs service. 1:1 with the Python
/// jobs_client.auth.mint_m2m_token: claims are exactly iat, exp, sub, tenant_id, scope
/// (scope = "admin" iff tenant_id == "_org"). No iss/aud — the jobs service validates
/// via JWKS keyed on the kid header.</summary>
public sealed class M2mTokenMinter
{
    private readonly RSA _key;
    private readonly string _ownerService, _tenantId, _kid;
    private readonly TimeProvider _clock;

    public int TtlSeconds { get; }

    public M2mTokenMinter(RSA privateKey, string ownerService, string tenantId,
        int ttlSeconds = 3600, string? kid = null, TimeProvider? timeProvider = null)
    {
        _key = privateKey;
        _ownerService = ownerService;
        _tenantId = tenantId;
        TtlSeconds = ttlSeconds;
        _kid = kid ?? Kid.Compute(privateKey);
        _clock = timeProvider ?? TimeProvider.System;
    }

    public string Mint() => MintAt(_clock.GetUtcNow());

    /// <summary>Mints a token stamped at the given instant. Used by <c>TokenProvider</c>
    /// to keep <c>iat</c>/<c>exp</c> aligned with an injected <see cref="TimeProvider"/>.</summary>
    public string MintAt(DateTimeOffset at)
    {
        var now = at.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object?>
        {
            ["iat"] = now,
            ["exp"] = now + TtlSeconds,
            ["sub"] = _ownerService,
            ["tenant_id"] = _tenantId,
            ["scope"] = _tenantId == "_org" ? "admin" : "tenant",
        };
        return Jwt.SignRs256(claims, _key, _kid);
    }
}
