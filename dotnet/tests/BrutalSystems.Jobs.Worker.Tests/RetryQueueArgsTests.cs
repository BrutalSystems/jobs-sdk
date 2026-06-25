using System.Text.Json;
using Xunit;

namespace BrutalSystems.Jobs.Worker.Tests;

public class RetryQueueArgsTests
{
    [Fact]
    public void Build_sets_ttl_and_dlx_back_to_work_queue()
    {
        var args = RetryQueueArguments.Build("jobs.brokenhip-be.brokenhip.ping", 30000);
        Assert.Equal(30000, args["x-message-ttl"]);
        Assert.Equal("jobs", args["x-dead-letter-exchange"]);
        Assert.Equal("jobs.brokenhip-be.brokenhip.ping", args["x-dead-letter-routing-key"]);
    }

    // --- Per-job prefetch seam tests (fix: per-job channels + per-job QoS) ---

    [Fact]
    public void PerJobPrefetch_uses_policy_concurrency_when_present()
    {
        var policy = JsonDocument.Parse("""{"concurrency": 4}""").RootElement;
        Assert.Equal(4, WarmConsumer.PerJobPrefetch(policy, prefetchDefault: 1));
    }

    [Fact]
    public void PerJobPrefetch_falls_back_to_default_when_policy_missing_concurrency()
    {
        var policy = JsonDocument.Parse("""{"timeout_seconds": 60}""").RootElement;
        Assert.Equal(3, WarmConsumer.PerJobPrefetch(policy, prefetchDefault: 3));
    }

    [Fact]
    public void PerJobPrefetch_falls_back_to_default_when_policy_is_null_element()
    {
        // TryGetProperty returns default JsonElement (Undefined kind) when policy key absent.
        var policy = default(JsonElement);
        Assert.Equal(2, WarmConsumer.PerJobPrefetch(policy, prefetchDefault: 2));
    }

    [Fact]
    public void PerJobPrefetch_jobs_get_independent_values()
    {
        // Confirm two jobs with different concurrencies get independent prefetch values —
        // the core invariant of the per-job-channel fix.
        var pol1 = JsonDocument.Parse("""{"concurrency": 2}""").RootElement;
        var pol2 = JsonDocument.Parse("""{"concurrency": 8}""").RootElement;
        Assert.Equal(2, WarmConsumer.PerJobPrefetch(pol1, prefetchDefault: 1));
        Assert.Equal(8, WarmConsumer.PerJobPrefetch(pol2, prefetchDefault: 1));
    }
}
