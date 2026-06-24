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
        Assert.Equal(1.0, back.Args["x"]);
    }

    [Fact]
    public void Args_nested_payload_survives_round_trip()
    {
        var env = new MessageEnvelope
        {
            RunId = "r2", JobId = "j2", TenantId = "_org", OwnerService = "brokenhip-be",
            JobName = "brokenhip.complex",
            Args = new Dictionary<string, object?>
            {
                ["count"]   = 42L,
                ["ratio"]   = 1.5,
                ["flag"]    = true,
                ["nothing"] = null,
                ["tags"]    = new List<object?> { "a", "b", 99L },
                ["meta"]    = new Dictionary<string, object?> { ["k"] = "v", ["n"] = 7L },
            },
            Attempt = 1,
            DispatchedAt = new DateTimeOffset(2026, 6, 24, 12, 0, 0, TimeSpan.Zero),
        };

        var back = MessageEnvelope.FromJsonBytes(env.ToJsonBytes());

        Assert.Equal(42.0,  back.Args["count"]);
        Assert.Equal(1.5,   back.Args["ratio"]);
        Assert.Equal(true,  back.Args["flag"]);
        Assert.Null(back.Args["nothing"]);

        var tags = Assert.IsType<List<object?>>(back.Args["tags"]);
        Assert.Equal(3, tags.Count);
        Assert.Equal("a",   tags[0]);
        Assert.Equal("b",   tags[1]);
        Assert.Equal(99.0,  tags[2]);

        var meta = Assert.IsType<Dictionary<string, object?>>(back.Args["meta"]);
        Assert.Equal("v",  meta["k"]);
        Assert.Equal(7.0,  meta["n"]);
    }

    [Fact]
    public void Decodes_known_jobs_service_payload()
    {
        const string json = """
            {
                "run_id": "run-abc123",
                "job_id": "job-def456",
                "tenant_id": "acme",
                "owner_service": "order-svc",
                "job_name": "order.fulfil",
                "attempt": 3,
                "dispatched_at": "2026-06-24T09:00:00+00:00",
                "args": {
                    "order_id": "ORD-999",
                    "quantity": 5
                }
            }
            """;

        var env = MessageEnvelope.FromJsonBytes(Encoding.UTF8.GetBytes(json));

        Assert.Equal("run-abc123",  env.RunId);
        Assert.Equal("job-def456",  env.JobId);
        Assert.Equal("acme",        env.TenantId);
        Assert.Equal("order-svc",   env.OwnerService);
        Assert.Equal("order.fulfil", env.JobName);
        Assert.Equal(3,             env.Attempt);
        Assert.Equal(new DateTimeOffset(2026, 6, 24, 9, 0, 0, TimeSpan.Zero), env.DispatchedAt);
        Assert.Equal("ORD-999",     env.Args["order_id"]);
        Assert.Equal(5.0,           env.Args["quantity"]);
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
