using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Prometheus;

[assembly: InternalsVisibleTo("BrutalSystems.Jobs.Worker.Tests")]

namespace BrutalSystems.Jobs.Worker;

/// <summary>Monotonic last-progress timestamp. Bumped by the consumer loop, read by the
/// health endpoint. Port of jobs_client.observability.Heartbeat.</summary>
public sealed class Heartbeat
{
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private DateTimeOffset _last;

    public Heartbeat(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _last = _clock.GetUtcNow();
    }

    public void Beat() { lock (_gate) _last = _clock.GetUtcNow(); }
    public TimeSpan SinceLast() { lock (_gate) return _clock.GetUtcNow() - _last; }
}

/// <summary>Warm-consumer metrics on a private registry. Metric NAMES match jobs-service's so
/// Prometheus aggregates across targets. Port of jobs_client.observability.</summary>
public sealed class JobsMetrics
{
    private readonly CollectorRegistry _registry = Metrics.NewCustomRegistry();

    public Counter QueueConsumeTotal { get; }
    public Histogram QueueAttemptCount { get; }
    public Gauge QueueConnectionUp { get; }
    public Gauge QueuePendingMessages { get; }
    public Gauge QueueDlqMessages { get; }
    public Histogram WarmHandlerDurationSeconds { get; }

    public JobsMetrics()
    {
        var f = Metrics.WithCustomRegistry(_registry);
        QueueConsumeTotal = f.CreateCounter("jobs_queue_consume_total", "Queue consumer outcomes",
            new CounterConfiguration { LabelNames = ["job_name", "dispatch_mode", "outcome"] });
        QueueAttemptCount = f.CreateHistogram("jobs_queue_attempt_count", "Attempt number at terminal disposition",
            new HistogramConfiguration { LabelNames = ["job_name", "dispatch_mode"] });
        QueueConnectionUp = f.CreateGauge("jobs_queue_connection_up", "1 if the queue connection is up, 0 if reconnecting",
            new GaugeConfiguration { LabelNames = ["endpoint"] });
        QueuePendingMessages = f.CreateGauge("jobs_queue_pending_messages", "Messages waiting in each work queue",
            new GaugeConfiguration { LabelNames = ["job_name", "dispatch_mode"] });
        QueueDlqMessages = f.CreateGauge("jobs_queue_dlq_messages", "Messages in each dead-letter queue",
            new GaugeConfiguration { LabelNames = ["job_name", "dispatch_mode"] });
        WarmHandlerDurationSeconds = f.CreateHistogram("jobs_warm_handler_duration_seconds", "Time inside the warm handler callable",
            new HistogramConfiguration { LabelNames = ["job_name", "outcome"] });
    }

    public string Render()
    {
        // Sync-over-async is fine here: /metrics is scraped rarely and off the hot path.
        using var ms = new MemoryStream();
        _registry.CollectAndExportAsTextAsync(ms).GetAwaiter().GetResult();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public (int Status, string Body) Health(int stalenessSeconds, Heartbeat hb)
        => hb.SinceLast() >= TimeSpan.FromSeconds(stalenessSeconds) ? (503, "stale\n") : (200, "ok\n");
}

/// <summary>HttpListener serving /metrics + /healthz. /healthz is heartbeat-checked so an
/// event-loop stall trips k8s liveness even while /metrics still answers.</summary>
public static class MetricsServer
{
    /// <summary>Allows tests to read the OS-assigned port when port 0 is passed to Start.</summary>
    internal interface IHavePort
    {
        int Port { get; }
    }

    public static IDisposable Start(int port, JobsMetrics metrics, Heartbeat heartbeat, int stalenessSeconds,
        string? host = null)
    {
        // Default to all interfaces — k8s liveness probes the pod IP, not loopback. The `+` strong
        // wildcard needs a urlacl on Windows; a Windows dev can pass METRICS_HOST=localhost instead.
        host ??= Environment.GetEnvironmentVariable("METRICS_HOST") ?? "+";

        // When port 0 is requested, pick an ephemeral port using a TcpListener probe.
        // HttpListener does not support port 0 directly (it uses the HTTP API, not TCP sockets).
        int boundPort = port == 0 ? FindFreePort() : port;

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{host}:{boundPort}/");
        listener.Start();

        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch { break; }
                var h = metrics.Health(stalenessSeconds, heartbeat);
                var (status, body, contentType) = ctx.Request.Url?.AbsolutePath switch
                {
                    "/metrics" => (200, metrics.Render(), "text/plain; version=0.0.4"),
                    "/healthz" => (h.Status, h.Body, "text/plain"),
                    _ => (404, "", "text/plain"),
                };
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = contentType;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
        return new Stopper(listener, cts, boundPort);
    }

    /// <summary>For test/ephemeral use only — production must always pass a real configured port (never 0), as the probe binds loopback only.</summary>
    private static int FindFreePort()
    {
        // Bind to port 0 to let the OS allocate an ephemeral port, then release it.
        // There is a tiny TOCTOU window, but it is negligible for test use.
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int p = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return p;
    }

    private sealed class Stopper(HttpListener listener, CancellationTokenSource cts, int port)
        : IDisposable, IHavePort
    {
        public int Port => port;
        public void Dispose() { cts.Cancel(); listener.Close(); }
    }
}
