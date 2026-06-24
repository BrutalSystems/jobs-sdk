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
}
