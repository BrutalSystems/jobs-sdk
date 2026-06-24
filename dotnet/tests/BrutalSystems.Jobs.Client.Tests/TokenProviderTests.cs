using System.Security.Cryptography;
using BrutalSystems.Jobs.Core;
using Xunit;

namespace BrutalSystems.Jobs.Client.Tests;

public class TokenProviderTests
{
    [Fact]
    public void Caches_token_until_within_60s_of_expiry()
    {
        using var rsa = RSA.Create(2048);
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var minter = new M2mTokenMinter(rsa, "svc", "_org", ttlSeconds: 3600);
        var provider = new TokenProvider(minter, clock);

        var first = provider.GetToken();
        clock.Advance(TimeSpan.FromSeconds(3000));   // still > 60s from expiry
        Assert.Equal(first, provider.GetToken());

        clock.Advance(TimeSpan.FromSeconds(560));     // now within 60s of the 3600s expiry
        Assert.NotEqual(first, provider.GetToken());
    }
}

// Minimal deterministic TimeProvider for tests.
file sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
