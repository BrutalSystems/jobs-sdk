using System.Security.Cryptography;
using Xunit;

namespace BrutalSystems.Jobs.Core.Tests;

public class JwksTests
{
    [Fact]
    public void Export_emits_one_rsa_sig_key_with_kid()
    {
        using var rsa = TestKeys.NewRsa();
        var doc = Jwks.Export(rsa);
        var key = Assert.Single(doc.Keys);
        Assert.Equal("RSA", key.Kty);
        Assert.Equal("sig", key.Use);
        Assert.Equal("RS256", key.Alg);
        Assert.Equal(Kid.Compute(rsa), key.Kid);
        Assert.False(string.IsNullOrEmpty(key.N));
        Assert.False(string.IsNullOrEmpty(key.E));
    }

    [Fact]
    public void Export_honors_explicit_kid()
    {
        using var rsa = TestKeys.NewRsa();
        var doc = Jwks.Export(rsa, kid: "static-kid-123");
        Assert.Equal("static-kid-123", doc.Keys[0].Kid);
    }

    [Fact]
    public void Export_n_e_decode_back_to_modulus_and_exponent()
    {
        using var rsa = TestKeys.NewRsa();
        var p = rsa.ExportParameters(false);
        var doc = Jwks.Export(rsa);
        var key = Assert.Single(doc.Keys);
        Assert.Equal(p.Modulus, FromBase64Url(key.N));
        Assert.Equal(p.Exponent, FromBase64Url(key.E));
    }

    private static byte[] FromBase64Url(string s)
    {
        var base64 = s.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        return Convert.FromBase64String(base64);
    }
}
