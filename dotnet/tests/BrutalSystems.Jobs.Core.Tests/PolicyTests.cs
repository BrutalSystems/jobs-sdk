using System.Text.Json;
using Xunit;

namespace BrutalSystems.Jobs.Core.Tests;

public class PolicyTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Defaults_match_jobs_core()
    {
        var p = new Policy { ExecutionMode = "in_process" };
        Assert.Equal(1, p.Concurrency);
        Assert.Equal(3600, p.TimeoutSeconds);
        Assert.False(p.PreferSpot);
        Assert.Equal("any", p.NodeArch);
        Assert.Equal("direct", p.DispatchMode);
        Assert.Empty(p.EnvFrom);
        Assert.Empty(p.ConfigMapEnvFrom);
    }

    [Fact]
    public void Serializes_field_names_as_snake_case()
    {
        var json = JsonSerializer.Serialize(
            new Policy { ExecutionMode = "container", DispatchMode = "warm-queued" }, Json);
        Assert.Contains("\"execution_mode\":\"container\"", json);
        Assert.Contains("\"dispatch_mode\":\"warm-queued\"", json);
        Assert.Contains("\"config_map_env_from\":[]", json);
    }

    [Fact]
    public void Unknown_field_is_rejected()
    {
        var ex = Record.Exception(() =>
            JsonSerializer.Deserialize<Policy>("{\"execution_mode\":\"in_process\",\"bogus\":1}", Json));
        Assert.IsType<JsonException>(ex);
    }
}
