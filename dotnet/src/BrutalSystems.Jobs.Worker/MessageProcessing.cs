using BrutalSystems.Jobs.Client;
using BrutalSystems.Jobs.Core;

namespace BrutalSystems.Jobs.Worker;

/// <summary>One AMQP delivery, abstracted so the processing ladder is testable without a broker.</summary>
public interface IIncomingDelivery
{
    ReadOnlyMemory<byte> Body { get; }
    IReadOnlyDictionary<string, object?> Headers { get; }
    Task AckAsync(CancellationToken ct);
}

/// <summary>Publishes envelopes to the per-Job retry / dead queues.</summary>
public interface IRetryPublisher
{
    Task PublishRetryAsync(MessageEnvelope env, int attempt, string reason, CancellationToken ct);
    Task PublishDeadAsync(MessageEnvelope env, int attempts, string reason, CancellationToken ct);
}

/// <summary>The warm-queued message-processing ladder. Port of warm_consumer.process_message:
/// start_run → handler (with timeout) → finish_run → ack; TransientException → retry/DLQ.</summary>
public sealed class MessageProcessor(HandlerRegistry registry, JobsClient client, JobsMetrics metrics)
{
    private const string Dm = "warm-queued";

    public async Task ProcessAsync(IIncomingDelivery delivery, IRetryPublisher publisher,
        int maxAttempts, TimeSpan timeout, CancellationToken ct = default)
    {
        MessageEnvelope env;
        try
        {
            env = MessageEnvelope.FromJsonBytes(delivery.Body.Span);
        }
        catch (FormatException)
        {
            metrics.QueueConsumeTotal.WithLabels("_unparsed", Dm, "dropped").Inc();
            await delivery.AckAsync(ct);
            return;
        }

        if (!registry.TryGet(env.JobName, out var handler))
        {
            metrics.QueueConsumeTotal.WithLabels(env.JobName, Dm, "dropped").Inc();
            await delivery.AckAsync(ct);
            return;
        }

        using var _ = LogContext.Bind(env.RunId, env.JobId, env.TenantId, env.OwnerService);
        await client.StartRunAsync(env.JobId, idempotencyKey: env.RunId, trigger: "queue", ct: ct);

        var started = TimeProvider.System.GetTimestamp();
        IReadOnlyDictionary<string, object?>? summary = null;
        var status = "succeeded";
        int? exitCode = 0;
        var timedOut = false;
        var attempt = HeaderAttempt(delivery, env);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                summary = await handler(env.Args, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                timedOut = true;
                status = "failed";
                exitCode = 1;
                summary = new Dictionary<string, object?> { ["timeout"] = true, ["timeout_seconds"] = (int)timeout.TotalSeconds };
            }
        }
        catch (TransientException exc)
        {
            if (attempt < maxAttempts)
            {
                await publisher.PublishRetryAsync(env, attempt + 1, exc.Message, ct);
                metrics.QueueConsumeTotal.WithLabels(env.JobName, Dm, "retry").Inc();
                await delivery.AckAsync(ct);
                return;
            }
            await publisher.PublishDeadAsync(env, attempt, exc.Message, ct);
            status = "failed";
            exitCode = 1;
            summary = new Dictionary<string, object?> { ["error"] = exc.Message, ["dlq"] = true, ["attempts"] = attempt };
        }
        catch (Exception exc)
        {
            status = "failed";
            exitCode = 1;
            summary = new Dictionary<string, object?> { ["error"] = exc.Message };
        }

        var elapsed = TimeProvider.System.GetElapsedTime(started);
        var handlerOutcome = timedOut ? "timeout" : status == "failed" ? "failed" : "succeeded";
        metrics.WarmHandlerDurationSeconds.WithLabels(env.JobName, handlerOutcome).Observe(elapsed.TotalSeconds);

        var isDlq = summary is not null && summary.TryGetValue("dlq", out var d) && d is true;
        metrics.QueueConsumeTotal.WithLabels(env.JobName, Dm, isDlq ? "dlq" : "ack").Inc();
        metrics.QueueAttemptCount.WithLabels(env.JobName, Dm).Observe(attempt);

        await client.FinishRunAsync(env.JobId, env.RunId, status, exitCode, summary, ct);
        await delivery.AckAsync(ct);
    }

    private static int HeaderAttempt(IIncomingDelivery delivery, MessageEnvelope env)
        => delivery.Headers.TryGetValue("attempt", out var v) && v is not null && int.TryParse(v.ToString(), out var n)
            ? n : env.Attempt;
}
