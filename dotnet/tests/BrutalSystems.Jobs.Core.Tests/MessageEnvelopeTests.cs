using System.Text;
using Xunit;

namespace BrutalSystems.Jobs.Core.Tests;

public class MessageEnvelopeTests
{
    [Fact]
    public void Round_trips_through_json_bytes()
    {
        var env = new MessageEnvelope
        {
            RunId = "r1", JobId = "j1", TenantId = "_org", OwnerService = "brokenhip-be",
            JobName = "brokenhip.ping", Args = new Dictionary<string, object?> { ["x"] = 1 },
            Attempt = 2, DispatchedAt = new DateTimeOffset(2026, 6, 24, 0, 0, 0, TimeSpan.Zero),
        };
        var back = MessageEnvelope.FromJsonBytes(env.ToJsonBytes());
        Assert.Equal("r1", back.RunId);
        Assert.Equal("brokenhip.ping", back.JobName);
        Assert.Equal(2, back.Attempt);
    }

    [Fact]
    public void Json_uses_snake_case_keys()
    {
        var json = Encoding.UTF8.GetString(new MessageEnvelope
        {
            RunId = "r", JobId = "j", TenantId = "t", OwnerService = "o", JobName = "n",
            DispatchedAt = DateTimeOffset.UnixEpoch,
        }.ToJsonBytes());
        Assert.Contains("\"run_id\":\"r\"", json);
        Assert.Contains("\"owner_service\":\"o\"", json);
        Assert.Contains("\"attempt\":1", json);
    }

    [Fact]
    public void Malformed_body_throws_FormatException()
        => Assert.Throws<FormatException>(() => MessageEnvelope.FromJsonBytes(Encoding.UTF8.GetBytes("{not json")));
}
