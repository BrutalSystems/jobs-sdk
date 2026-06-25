using System.Security.Cryptography;
using Xunit;

namespace BrutalSystems.Jobs.Core.Tests;

public class KidTests
{
    [Fact]
    public void Compute_is_16_chars_and_url_safe()
    {
        using var rsa = TestKeys.NewRsa();
        var kid = Kid.Compute(rsa);
        Assert.Equal(16, kid.Length);
        Assert.DoesNotContain('+', kid);
        Assert.DoesNotContain('/', kid);
        Assert.DoesNotContain('=', kid);
    }

    [Fact]
    public void Compute_derives_from_public_half_only()
    {
        using var rsa = TestKeys.NewRsa();
        using var pub = RSA.Create();
        pub.ImportSubjectPublicKeyInfo(rsa.ExportSubjectPublicKeyInfo(), out _);
        Assert.Equal(Kid.Compute(rsa), Kid.Compute(pub));
    }
}
