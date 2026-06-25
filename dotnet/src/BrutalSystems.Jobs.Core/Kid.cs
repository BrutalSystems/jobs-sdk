using System.Security.Cryptography;

namespace BrutalSystems.Jobs.Core;

public static class Kid
{
    /// <summary>JWKS key id: first 16 chars of URL-safe base64 (no padding) of SHA-256 of
    /// the SubjectPublicKeyInfo DER. Byte-identical to the Python SDK's _compute_kid_from_pem.</summary>
    public static string Compute(RSA key)
    {
        var der = key.ExportSubjectPublicKeyInfo();
        var hash = SHA256.HashData(der);
        return Jwt.B64Url(hash)[..16];
    }
}
