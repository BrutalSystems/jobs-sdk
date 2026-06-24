using System.Net;
using System.Text.Json;
using BrutalSystems.Jobs.Client;
using BrutalSystems.Jobs.Core;

namespace BrutalSystems.Jobs.Worker;

public sealed record WarmConsumerConfig(
    string AmqpUrl,
    string JobsServiceUrl,
    string OwnerService,
    string TenantId,
    int PrefetchDefault = 1,
    int MaxAttempts = 5,
    int MetricsPort = 9102,
    int LivenessStalenessSeconds = 120)
{
    public static WarmConsumerConfig FromEnv()
    {
        static string Required(string name) =>
            Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"{name} env var required by warm consumer");
        static int IntEnv(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;

        return new WarmConsumerConfig(
            AmqpUrl: Required("AMQP_URL"),
            JobsServiceUrl: Required(PodEnv.ServiceUrl),
            OwnerService: Required("OWNER_SERVICE"),
            TenantId: Environment.GetEnvironmentVariable("WARM_TENANT_ID") ?? "_org",
            PrefetchDefault: IntEnv("WARM_PREFETCH_DEFAULT", 1),
            MaxAttempts: IntEnv("WARM_MAX_ATTEMPTS", 5),
            MetricsPort: IntEnv("METRICS_PORT", 9102),
            LivenessStalenessSeconds: IntEnv("LIVENESS_STALENESS_SECONDS", 120));
    }
}

public static class WarmDiscovery
{
    private static readonly HashSet<HttpStatusCode> Retryable =
    [
        HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden,
        HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout,
    ];

    /// <summary>Query jobs-service for this owner's warm-queued Jobs, retrying transient
    /// cold-start failures with exponential backoff. Port of warm_consumer.discover_warm_jobs.</summary>
    public static async Task<IReadOnlyList<JsonElement>> DiscoverWarmJobsAsync(
        JobsClient client, string ownerService, int maxAttempts = 8,
        Func<int, Task>? delay = null, CancellationToken ct = default)
    {
        delay ??= ms => Task.Delay(ms, ct);
        var delayMs = 500;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await client.ListJobsFilteredAsync(
                    ownerService: ownerService, dispatchMode: "warm-queued", ct: ct);
            }
            catch (HttpRequestException exc)
            {
                var transient = exc.StatusCode is null || Retryable.Contains(exc.StatusCode.Value);
                if (!transient || attempt == maxAttempts) throw;
            }
            await delay(delayMs);
            delayMs = Math.Min(delayMs * 2, 8000);
        }
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>Boot-time check: every discovered warm Job has a registered handler.
    /// Port of warm_consumer.validate_handlers.</summary>
    public static void ValidateHandlers(IReadOnlyList<JsonElement> jobs, HandlerRegistry registry)
    {
        foreach (var job in jobs)
        {
            var name = job.GetProperty("name").GetString()!;
            if (!registry.Contains(name))
                throw new InvalidOperationException(
                    $"warm Job '{name}' registered but no handler registered; call " +
                    $"Register(\"{name}\", ...) on the HandlerRegistry before starting the consumer");
        }
    }
}
