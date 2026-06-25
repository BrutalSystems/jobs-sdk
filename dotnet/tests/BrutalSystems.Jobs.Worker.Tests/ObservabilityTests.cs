using System.Net;
using Xunit;

namespace BrutalSystems.Jobs.Worker.Tests;

public class ObservabilityTests
{
    [Fact]
    public void Metric_names_carry_jobs_prefix_in_render()
    {
        var m = new JobsMetrics();
        m.QueueConsumeTotal.WithLabels("brokenhip.ping", "warm-queued", "ack").Inc();
        var text = m.Render();
        Assert.Contains("jobs_queue_consume_total", text);
        Assert.Contains("jobs_warm_handler_duration_seconds", text);
    }

    [Fact]
    public void Health_is_200_when_fresh_503_when_stale()
    {
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        var hb = new Heartbeat(clock);
        var m = new JobsMetrics();

        Assert.Equal(200, m.Health(120, hb).Status);
        clock.Advance(TimeSpan.FromSeconds(121));
        var (status, body) = m.Health(120, hb);
        Assert.Equal(503, status);
        Assert.Equal("stale\n", body);
    }

    [Fact]
    public async Task MetricsServer_healthz_returns_ok_when_fresh()
    {
        var m = new JobsMetrics();
        var hb = new Heartbeat();
        using var server = MetricsServer.Start(0, m, hb, 120, "localhost");

        int port = ((MetricsServer.IHavePort)server).Port;
        using var client = new HttpClient();
        var resp = await client.GetAsync($"http://localhost:{port}/healthz");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("ok\n", body);
    }

    [Fact]
    public async Task MetricsServer_metrics_exposes_registered_metric_names()
    {
        var m = new JobsMetrics();
        m.QueueConsumeTotal.WithLabels("test.job", "warm-queued", "ack").Inc();
        var hb = new Heartbeat();
        using var server = MetricsServer.Start(0, m, hb, 120, "localhost");

        int port = ((MetricsServer.IHavePort)server).Port;
        using var client = new HttpClient();
        var resp = await client.GetAsync($"http://localhost:{port}/metrics");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("jobs_queue_consume_total", body);
        Assert.Contains("jobs_warm_handler_duration_seconds", body);
    }

    [Fact]
    public async Task MetricsServer_healthz_returns_503_when_stale()
    {
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        var hb = new Heartbeat(clock);
        var m = new JobsMetrics();
        clock.Advance(TimeSpan.FromSeconds(121));

        using var server = MetricsServer.Start(0, m, hb, 120, "localhost");
        int port = ((MetricsServer.IHavePort)server).Port;
        using var client = new HttpClient();
        var resp = await client.GetAsync($"http://localhost:{port}/healthz");
        var body = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Equal("stale\n", body);
    }

    [Fact]
    public void Heartbeat_SinceLast_reflects_elapsed_time()
    {
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        var hb = new Heartbeat(clock);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(hb.SinceLast() >= TimeSpan.FromSeconds(30));
        hb.Beat();
        Assert.True(hb.SinceLast() < TimeSpan.FromSeconds(1));
    }
}

file sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
