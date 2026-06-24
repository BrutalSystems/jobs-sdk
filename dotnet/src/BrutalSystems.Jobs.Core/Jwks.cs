using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace BrutalSystems.Jobs.Core;

public sealed record JwkKey(
    [property: JsonPropertyName("kty")] string Kty,
    [property: JsonPropertyName("use")] string Use,
    [property: JsonPropertyName("alg")] string Alg,
    [property: JsonPropertyName("kid")] string Kid,
    [property: JsonPropertyName("n")] string N,
    [property: JsonPropertyName("e")] string E);

public sealed record JwksDocument(
    [property: JsonPropertyName("keys")] IReadOnlyList<JwkKey> Keys);

public static class Jwks
{
    /// <summary>RFC 7517 JWKS for an RSA public key. kid defaults to Kid.Compute but can be
    /// overridden (brokenhip may serve a static kid).</summary>
    public static JwksDocument Export(RSA publicKey, string? kid = null)
    {
        var p = publicKey.ExportParameters(false);
        var entry = new JwkKey(
            Kty: "RSA", Use: "sig", Alg: "RS256",
            Kid: kid ?? Kid.Compute(publicKey),
            N: Jwt.B64Url(p.Modulus!),
            E: Jwt.B64Url(p.Exponent!));
        return new JwksDocument(new[] { entry });
    }
}
