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
        // Pass the same fake clock to both minter and provider so determinism
        // does not depend on TokenProvider calling MintAt with its own clock.
        var minter = new M2mTokenMinter(rsa, "svc", "_org", ttlSeconds: 3600, timeProvider: clock);
        var provider = new TokenProvider(minter, clock);

        var first = provider.GetToken();
        clock.Advance(TimeSpan.FromSeconds(3000));   // still > 60s from expiry
        Assert.Equal(first, provider.GetToken());

        clock.Advance(TimeSpan.FromSeconds(560));     // now within 60s of the 3600s expiry
        Assert.NotEqual(first, provider.GetToken());
    }

    // Boundary: production rule is strict < (expiry - now < 60s), so:
    //   3540s elapsed  → exactly 60s remaining → NOT re-minted (same token)
    //   3541s elapsed  → exactly 59s remaining → IS re-minted (different token)
    [Fact]
    public void RefreshWindow_boundary_strict_less_than_60s()
    {
        using var rsa = RSA.Create(2048);
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var minter = new M2mTokenMinter(rsa, "svc", "_org", ttlSeconds: 3600, timeProvider: clock);
        var provider = new TokenProvider(minter, clock);

        var first = provider.GetToken();  // minted at t=0, expires at t=3600

        // At t=3540: exactly 60s remain → (3600-3540)=60 which is NOT < 60 → same token
        clock.Advance(TimeSpan.FromSeconds(3540));
        Assert.Equal(first, provider.GetToken());

        // At t=3541: exactly 59s remain → (3600-3541)=59 which IS < 60 → re-minted
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = provider.GetToken();
        Assert.NotEqual(first, second);
    }
}

// Minimal deterministic TimeProvider for tests.
file sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
