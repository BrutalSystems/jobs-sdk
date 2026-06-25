using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace BrutalSystems.Jobs.Core.Tests;

public class M2mTokenMinterTests
{
    private static JsonElement DecodePayload(string jwt)
    {
        var part = jwt.Split('.')[1];
        var pad = part.Replace('-', '+').Replace('_', '/');
        pad = pad.PadRight(pad.Length + (4 - pad.Length % 4) % 4, '=');
        return JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(pad));
    }

    private static JsonElement DecodeHeader(string jwt)
    {
        var part = jwt.Split('.')[0];
        var pad = part.Replace('-', '+').Replace('_', '/');
        pad = pad.PadRight(pad.Length + (4 - pad.Length % 4) % 4, '=');
        return JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(pad));
    }

    [Fact]
    public void Mint_emits_exactly_the_five_m2m_claims()
    {
        using var rsa = TestKeys.NewRsa();
        var minter = new M2mTokenMinter(rsa, ownerService: "brokenhip-be", tenantId: "t-123");
        var payload = DecodePayload(minter.Mint());

        var names = new SortedSet<string>();
        foreach (var p in payload.EnumerateObject()) names.Add(p.Name);
        Assert.Equal(new[] { "exp", "iat", "scope", "sub", "tenant_id" }, names);
        Assert.Equal("brokenhip-be", payload.GetProperty("sub").GetString());
        Assert.Equal("t-123", payload.GetProperty("tenant_id").GetString());
    }

    [Fact]
    public void Scope_is_admin_for_org_tenant_else_tenant()
    {
        using var rsa = TestKeys.NewRsa();
        Assert.Equal("admin",
            DecodePayload(new M2mTokenMinter(rsa, "svc", "_org").Mint()).GetProperty("scope").GetString());
        Assert.Equal("tenant",
            DecodePayload(new M2mTokenMinter(rsa, "svc", "t-9").Mint()).GetProperty("scope").GetString());
    }

    [Fact]
    public void Header_carries_computed_kid_and_no_aud_or_iss()
    {
        using var rsa = TestKeys.NewRsa();
        var jwt = new M2mTokenMinter(rsa, "svc", "_org").Mint();
        Assert.Equal(Kid.Compute(rsa), DecodeHeader(jwt).GetProperty("kid").GetString());
        var payload = DecodePayload(jwt);
        Assert.False(payload.TryGetProperty("aud", out _));
        Assert.False(payload.TryGetProperty("iss", out _));
    }
}
