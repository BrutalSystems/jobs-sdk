using BrutalSystems.Jobs.Core;

namespace BrutalSystems.Jobs.Client;

/// <summary>Returns a cached m2m token, re-minting when missing or within 60s of expiry.
/// Port of jobs_client.auth.TokenProvider. Pass GetToken as the JobsClient token callback.</summary>
public sealed class TokenProvider
{
    private readonly M2mTokenMinter _minter;
    private readonly TimeProvider _clock;
    private string? _cached;
    private DateTimeOffset _expiry;

    public TokenProvider(M2mTokenMinter minter, TimeProvider? timeProvider = null)
    {
        _minter = minter;
        _clock = timeProvider ?? TimeProvider.System;
    }

    public string GetToken()
    {
        var now = _clock.GetUtcNow();
        if (_cached is null || _expiry - now < TimeSpan.FromSeconds(60))
        {
            _cached = _minter.MintAt(now);
            _expiry = now + TimeSpan.FromSeconds(_minter.TtlSeconds);
        }
        return _cached;
    }
}
