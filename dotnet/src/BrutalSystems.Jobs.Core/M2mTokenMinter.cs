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

    public int TtlSeconds { get; }

    public M2mTokenMinter(RSA privateKey, string ownerService, string tenantId,
        int ttlSeconds = 3600, string? kid = null)
    {
        _key = privateKey;
        _ownerService = ownerService;
        _tenantId = tenantId;
        TtlSeconds = ttlSeconds;
        _kid = kid ?? Kid.Compute(privateKey);
    }

    public string Mint()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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
