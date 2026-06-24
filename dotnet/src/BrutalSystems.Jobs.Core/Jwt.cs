using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BrutalSystems.Jobs.Core;

/// <summary>Minimal RS256 JWT encoder. Hand-built (not JwtSecurityTokenHandler) so the
/// emitted claim set is EXACTLY what the caller passes — no auto-injected nbf/jti — to
/// match the Python reference (pyjwt.encode with explicit claims).</summary>
internal static class Jwt
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    public static string SignRs256(IReadOnlyDictionary<string, object?> claims, RSA key, string kid)
    {
        var header = new Dictionary<string, object?> { ["alg"] = "RS256", ["typ"] = "JWT", ["kid"] = kid };
        var headerB64 = B64Url(JsonSerializer.SerializeToUtf8Bytes(header, Opts));
        var payloadB64 = B64Url(JsonSerializer.SerializeToUtf8Bytes(claims, Opts));
        var signingInput = $"{headerB64}.{payloadB64}";
        var sig = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{B64Url(sig)}";
    }

    public static string B64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
