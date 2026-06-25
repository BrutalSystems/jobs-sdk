using System.Text.Json;
using BrutalSystems.Jobs.Client;
using BrutalSystems.Jobs.Core;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace BrutalSystems.Jobs.Worker;

public static class RetryQueueArguments
{
    /// <summary>Retry-queue args: messages expire after the TTL and dead-letter back to the
    /// work queue via the `jobs` exchange. Mirrors warm_consumer._consume_one_job.</summary>
    public static Dictionary<string, object?> Build(string workQueue, int retryTtlMs) => new()
    {
        ["x-message-ttl"] = retryTtlMs,
        ["x-dead-letter-exchange"] = QueueNames.ExchangeName,
        ["x-dead-letter-routing-key"] = workQueue,
    };
}

/// <summary>RabbitMQ-backed warm consumer. Port of jobs_client.warm_consumer (the _run loop,
/// _consume_one_job topology, and _poll_warm_queue_depths poller).
///
/// Each warm job gets its OWN channel with ConsumerDispatchConcurrency and BasicQos set to
/// that job's policy.concurrency — matching the Python _consume_one_job semantics exactly.
/// Publisher confirms are NOT enabled (parity with Python).</summary>
public sealed class WarmConsumer(
    WarmConsumerConfig config, JobsClient client, HandlerRegistry registry,
    JobsMetrics metrics, ILogger<WarmConsumer> logger)
{
    private static int RetryTtlMs =>
        int.TryParse(Environment.GetEnvironmentVariable("WARM_RETRY_TTL_MS"), out var v) ? v : 30000;

    /// <summary>Compute the prefetch/concurrency for a single job's policy. Exposed as a
    /// testable seam so the per-job prefetch behaviour can be pinned without a broker.</summary>
    internal static int PerJobPrefetch(JsonElement policy, int prefetchDefault)
        => PolicyInt(policy, "concurrency", prefetchDefault);

    public async Task RunAsync(CancellationToken ct)
    {
        var jobs = await WarmDiscovery.DiscoverWarmJobsAsync(client, config.OwnerService, ct: ct);
        if (jobs.Count == 0)
        {
            logger.LogWarning("no warm-queued Jobs registered for owner {Owner}; exiting", config.OwnerService);
            return;
        }
        WarmDiscovery.ValidateHandlers(jobs, registry);

        var heartbeat = new Heartbeat();
        using var _ = MetricsServer.Start(config.MetricsPort, metrics, heartbeat, config.LivenessStalenessSeconds);

        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunIterationAsync(jobs, heartbeat, ct);
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception exc)
            {
                metrics.QueueConnectionUp.WithLabels("warmworker").Set(0);
                heartbeat.Beat();
                logger.LogWarning(exc, "consumer iteration crashed; reconnecting in {Backoff}s", backoff.TotalSeconds);
                await Task.Delay(backoff, ct);
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
        }
    }

    private async Task RunIterationAsync(IReadOnlyList<JsonElement> jobs, Heartbeat heartbeat, CancellationToken ct)
    {
        var factory = new ConnectionFactory { Uri = new Uri(config.AmqpUrl) };
        await using var connection = await factory.CreateConnectionAsync(ct);
        metrics.QueueConnectionUp.WithLabels("warmworker").Set(1);

        // Declare the shared exchange once on a short-lived setup channel, then dispose it.
        // Each job channel will declare it again (idempotent) but this ensures it exists before
        // any per-job topology runs, matching the Python order.
        await using (var setupChannel = await connection.CreateChannelAsync(
            new CreateChannelOptions(false, false, null, null), ct))
        {
            await setupChannel.ExchangeDeclareAsync(QueueNames.ExchangeName, ExchangeType.Topic,
                durable: true, autoDelete: false, cancellationToken: ct);
        }

        // Open one channel per job. Each channel carries:
        //   ConsumerDispatchConcurrency = job.concurrency   (so deliveries run concurrently)
        //   BasicQos prefetch           = job.concurrency   (so the broker caps in-flight per-channel)
        // This matches Python's _consume_one_job: channel.set_qos(prefetch_count=concurrency)
        // called on a per-job channel, with publisher confirms disabled.
        var jobChannels = new List<IChannel>(jobs.Count);
        try
        {
            foreach (var job in jobs)
            {
                var name = job.GetProperty("name").GetString()!;
                var policy = job.TryGetProperty("policy", out var p) ? p : default;
                var jobConcurrency = PerJobPrefetch(policy, config.PrefetchDefault);
                var timeoutSeconds = PolicyInt(policy, "timeout_seconds", 3600);

                // Per-job channel: ConsumerDispatchConcurrency = this job's concurrency.
                // Publisher confirms stay disabled — parity with Python.
                var channelOptions = new CreateChannelOptions(
                    publisherConfirmationsEnabled: false,
                    publisherConfirmationTrackingEnabled: false,
                    outstandingPublisherConfirmationsRateLimiter: null,
                    consumerDispatchConcurrency: (ushort)jobConcurrency);
                var jobChannel = await connection.CreateChannelAsync(channelOptions, ct);
                jobChannels.Add(jobChannel);

                var work = QueueNames.Work(config.OwnerService, name, "warm-queued");
                var retry = QueueNames.Retry(work);
                var dead = QueueNames.Dead(work);

                // Declare exchange (idempotent) + this job's topology on its own channel.
                await jobChannel.ExchangeDeclareAsync(QueueNames.ExchangeName, ExchangeType.Topic,
                    durable: true, autoDelete: false, cancellationToken: ct);
                await jobChannel.QueueDeclareAsync(work, durable: true, exclusive: false, autoDelete: false,
                    cancellationToken: ct);
                await jobChannel.QueueDeclareAsync(retry, durable: true, exclusive: false, autoDelete: false,
                    arguments: RetryQueueArguments.Build(work, RetryTtlMs), cancellationToken: ct);
                await jobChannel.QueueDeclareAsync(dead, durable: true, exclusive: false, autoDelete: false,
                    cancellationToken: ct);
                await jobChannel.QueueBindAsync(work, QueueNames.ExchangeName, work, cancellationToken: ct);
                await jobChannel.QueueBindAsync(retry, QueueNames.ExchangeName, retry, cancellationToken: ct);
                await jobChannel.QueueBindAsync(dead, QueueNames.ExchangeName, dead, cancellationToken: ct);

                // QoS must be applied BEFORE consuming so the broker respects the per-channel cap.
                // Mirrors: await channel.set_qos(prefetch_count=concurrency) in _consume_one_job.
                await jobChannel.BasicQosAsync(0, (ushort)jobConcurrency, global: false, ct);

                // Each job's retry publisher uses that job's own channel — same channel context as Python.
                var publisher = new RabbitRetryPublisher(jobChannel);
                var processor = new MessageProcessor(registry, client, metrics);
                var consumer = new AsyncEventingBasicConsumer(jobChannel);
                consumer.ReceivedAsync += async (_, ea) =>
                {
                    heartbeat.Beat();
                    try
                    {
                        var delivery = new RabbitDelivery(jobChannel, ea);
                        await processor.ProcessAsync(delivery, publisher, config.MaxAttempts,
                            TimeSpan.FromSeconds(timeoutSeconds), ct);
                    }
                    catch (Exception exc)
                    {
                        // Infrastructure failure (e.g. start_run/finish_run HTTP error). Log and leave the
                        // delivery unacked for redelivery; never let it escape the consumer dispatcher
                        // (an unhandled exception there can tear down the channel).
                        logger.LogWarning(exc, "warm delivery for {Job} failed; left unacked for redelivery", name);
                    }
                };
                await jobChannel.BasicConsumeAsync(work, autoAck: false, consumer: consumer,
                    cancellationToken: ct);
            }

            var poller = PollDepthsAsync(connection, jobs, heartbeat, ct);
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            await poller;
        }
        finally
        {
            // Dispose all per-job channels on shutdown or cancellation.
            foreach (var ch in jobChannels)
            {
                try { await ch.DisposeAsync(); } catch { /* best-effort */ }
            }
        }
    }

    private async Task PollDepthsAsync(IConnection connection, IReadOnlyList<JsonElement> jobs,
        Heartbeat heartbeat, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Polling uses its OWN channel per cycle (not the consumers' channels): a passive
                // declare of a missing queue raises NOT_FOUND and closes the channel. Sharing a
                // consumer channel would kill its consumers. Mirrors Python _poll_warm_queue_depths.
                await using var ch = await connection.CreateChannelAsync(
                    new CreateChannelOptions(false, false, null, null), ct);
                foreach (var job in jobs)
                {
                    var name = job.GetProperty("name").GetString()!;
                    var work = QueueNames.Work(config.OwnerService, name, "warm-queued");
                    await SetDepth(ch, work, name, metrics.QueuePendingMessages, ct);
                    await SetDepth(ch, QueueNames.Dead(work), name, metrics.QueueDlqMessages, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception exc) { logger.LogDebug(exc, "depth poll cycle skipped"); }
            heartbeat.Beat();
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private static async Task SetDepth(IChannel ch, string queue, string jobName,
        Prometheus.Gauge gauge, CancellationToken ct)
    {
        try
        {
            var ok = await ch.QueueDeclarePassiveAsync(queue, ct);
            gauge.WithLabels(jobName, "warm-queued").Set(ok.MessageCount);
        }
        catch { /* a NOT_FOUND closes the channel; remaining reads this cycle skip, next cycle reopens a fresh channel. Work/dead queues are declared before polling starts, so in practice they exist. */ }
    }

    private static int PolicyInt(JsonElement policy, string key, int fallback)
        => policy.ValueKind == JsonValueKind.Object && policy.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
}

/// <summary>Adapts a RabbitMQ delivery to the broker-agnostic IIncomingDelivery seam.</summary>
internal sealed class RabbitDelivery(IChannel channel, BasicDeliverEventArgs ea) : IIncomingDelivery
{
    // Body must be copied: RabbitMQ.Client 7.x reuses the buffer after the handler returns.
    public ReadOnlyMemory<byte> Body { get; } = ea.Body.ToArray();
    public IReadOnlyDictionary<string, object?> Headers =>
        ea.BasicProperties.Headers is { } h
            ? h.ToDictionary(kv => kv.Key, kv => (object?)kv.Value)
            : new Dictionary<string, object?>();
    public Task AckAsync(CancellationToken ct) => channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct).AsTask();
}

/// <summary>Publishes envelopes to the retry / dead queues. Port of _publish_to_retry/_publish_to_dlq.
/// Each instance is bound to one job's channel so republish uses the same channel context as Python.</summary>
internal sealed class RabbitRetryPublisher(IChannel channel) : IRetryPublisher
{
    public Task PublishRetryAsync(MessageEnvelope env, int attempt, string reason, CancellationToken ct)
        => PublishAsync(QueueNames.Retry(QueueNames.Work(env.OwnerService, env.JobName, "warm-queued")),
            env with { Attempt = attempt }, attempt, reason, dlq: false, ct);

    public Task PublishDeadAsync(MessageEnvelope env, int attempts, string reason, CancellationToken ct)
        => PublishAsync(QueueNames.Dead(QueueNames.Work(env.OwnerService, env.JobName, "warm-queued")),
            env with { Attempt = attempts }, attempts, reason, dlq: true, ct);

    private async Task PublishAsync(string routingKey, MessageEnvelope env, int attempt, string reason,
        bool dlq, CancellationToken ct)
    {
        var headers = new Dictionary<string, object?>
        {
            ["attempt"] = attempt,
            ["last_error"] = reason.Length > 1024 ? reason[..1024] : reason,
        };
        if (dlq) headers["dlq"] = true;
        var props = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            MessageId = env.RunId,
            Headers = headers,
        };
        await channel.BasicPublishAsync(QueueNames.ExchangeName, routingKey, mandatory: false,
            basicProperties: props, body: env.ToJsonBytes(), cancellationToken: ct);
    }
}
