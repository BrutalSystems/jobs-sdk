using System.Net;
using System.Text;
using BrutalSystems.Jobs.Client;
using BrutalSystems.Jobs.Core;
using Xunit;

namespace BrutalSystems.Jobs.Worker.Tests;

public class MessageProcessorTests
{
    private static MessageEnvelope Env(int attempt = 1) => new()
    {
        RunId = "r1", JobId = "j1", TenantId = "_org", OwnerService = "brokenhip-be",
        JobName = "brokenhip.ping", Attempt = attempt, DispatchedAt = DateTimeOffset.UnixEpoch,
    };

    private sealed class FakeDelivery(byte[] body) : IIncomingDelivery
    {
        public ReadOnlyMemory<byte> Body { get; } = body;
        public IReadOnlyDictionary<string, object?> Headers { get; } = new Dictionary<string, object?>();
        public bool Acked { get; private set; }
        public Task AckAsync(CancellationToken ct) { Acked = true; return Task.CompletedTask; }
    }

    private sealed class FakePublisher : IRetryPublisher
    {
        public int Retries, Deads;
        public int LastRetryAttempt, LastDeadAttempts;
        public Task PublishRetryAsync(MessageEnvelope e, int a, string r, CancellationToken ct) { Retries++; LastRetryAttempt = a; return Task.CompletedTask; }
        public Task PublishDeadAsync(MessageEnvelope e, int a, string r, CancellationToken ct) { Deads++; LastDeadAttempts = a; return Task.CompletedTask; }
    }

    // start_run returns {run_id}; finish_run returns {status}. Enqueue per call the processor makes.
    private static (MessageProcessor proc, StubHandler stub, HandlerRegistry reg) Build(JobHandler handler)
    {
        var stub = new StubHandler();
        var client = new JobsClient(new HttpClient(stub), () => "tok", "https://jobs.example.com");
        var reg = new HandlerRegistry();
        reg.Register("brokenhip.ping", handler);
        return (new MessageProcessor(reg, client, new JobsMetrics()), stub, reg);
    }

    [Fact]
    public async Task Happy_path_starts_finishes_and_acks()
    {
        var (proc, stub, _) = Build((_, _) => Task.FromResult<IReadOnlyDictionary<string, object?>?>(
            new Dictionary<string, object?> { ["ok"] = true }));
        stub.Enqueue(HttpStatusCode.Created, "{\"run_id\":\"r1\"}")      // start_run
            .Enqueue(HttpStatusCode.OK, "{\"status\":\"succeeded\"}");   // finish_run
        var delivery = new FakeDelivery(Env().ToJsonBytes());
        var pub = new FakePublisher();

        await proc.ProcessAsync(delivery, pub, maxAttempts: 5, timeout: TimeSpan.FromSeconds(5));

        Assert.True(delivery.Acked);
        Assert.Equal(0, pub.Retries + pub.Deads);
        var startReq = Assert.Single(stub.Requests, r => r.Url.EndsWith("/runs") && r.Method == HttpMethod.Post);
        Assert.Contains("\"trigger\":\"queue\"", startReq.Body);
        Assert.Contains("\"run_id\":\"r1\"", startReq.Body);
        Assert.Contains(stub.Requests, r => r.Method == HttpMethod.Patch && r.Body.Contains("\"status\":\"succeeded\""));
    }

    [Fact]
    public async Task Transient_below_max_publishes_retry_and_acks_without_finish()
    {
        var (proc, stub, _) = Build((_, _) => throw new TransientException("try later"));
        stub.Enqueue(HttpStatusCode.Created, "{\"run_id\":\"r1\"}");   // start_run only
        var delivery = new FakeDelivery(Env(attempt: 1).ToJsonBytes());
        var pub = new FakePublisher();

        await proc.ProcessAsync(delivery, pub, maxAttempts: 5, timeout: TimeSpan.FromSeconds(5));

        Assert.Equal(1, pub.Retries);
        Assert.Equal(0, pub.Deads);
        Assert.Equal(2, pub.LastRetryAttempt);  // attempt=1 → retry at attempt+1=2
        Assert.True(delivery.Acked);
        Assert.DoesNotContain(stub.Requests, r => r.Method == HttpMethod.Patch);  // no finish_run
    }

    [Fact]
    public async Task Transient_at_max_publishes_dead_and_finishes_failed()
    {
        var (proc, stub, _) = Build((_, _) => throw new TransientException("dead"));
        stub.Enqueue(HttpStatusCode.Created, "{\"run_id\":\"r1\"}")
            .Enqueue(HttpStatusCode.OK, "{\"status\":\"failed\"}");
        var delivery = new FakeDelivery(Env(attempt: 5).ToJsonBytes());
        var pub = new FakePublisher();

        await proc.ProcessAsync(delivery, pub, maxAttempts: 5, timeout: TimeSpan.FromSeconds(5));

        Assert.Equal(1, pub.Deads);
        Assert.Equal(5, pub.LastDeadAttempts);  // attempt=5/max=5 → dead published with attempt=5, not 6
        Assert.Contains(stub.Requests, r => r.Method == HttpMethod.Patch && r.Body.Contains("\"status\":\"failed\""));
        Assert.True(delivery.Acked);
    }

    [Fact]
    public async Task Timeout_finishes_failed_with_timeout_summary()
    {
        var (proc, stub, _) = Build(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; });
        stub.Enqueue(HttpStatusCode.Created, "{\"run_id\":\"r1\"}")
            .Enqueue(HttpStatusCode.OK, "{\"status\":\"failed\"}");
        var delivery = new FakeDelivery(Env().ToJsonBytes());

        await proc.ProcessAsync(delivery, new FakePublisher(), maxAttempts: 5, timeout: TimeSpan.FromMilliseconds(50));

        var finish = Assert.Single(stub.Requests, r => r.Method == HttpMethod.Patch);
        Assert.Contains("\"timeout\":true", finish.Body);
        Assert.True(delivery.Acked);
    }

    [Fact]
    public async Task Malformed_body_is_dropped_and_acked_without_start()
    {
        var (proc, stub, _) = Build((_, _) => Task.FromResult<IReadOnlyDictionary<string, object?>?>(null));
        var delivery = new FakeDelivery(Encoding.UTF8.GetBytes("{not json"));

        await proc.ProcessAsync(delivery, new FakePublisher(), maxAttempts: 5, timeout: TimeSpan.FromSeconds(5));

        Assert.True(delivery.Acked);
        Assert.Empty(stub.Requests);   // never called start_run
    }

    [Fact]
    public async Task Handler_receives_native_arg_values_from_the_wire()
    {
        IReadOnlyDictionary<string, object?>? seen = null;
        var (proc, stub, _) = Build((args, _) =>
        {
            seen = args;
            return Task.FromResult<IReadOnlyDictionary<string, object?>?>(null);
        });
        stub.Enqueue(HttpStatusCode.Created, "{\"run_id\":\"r1\"}")
            .Enqueue(HttpStatusCode.OK, "{\"status\":\"succeeded\"}");
        var env = Env() with { Args = new Dictionary<string, object?> { ["note"] = "hi", ["count"] = 7 } };

        await proc.ProcessAsync(new FakeDelivery(env.ToJsonBytes()), new FakePublisher(),
            maxAttempts: 5, timeout: TimeSpan.FromSeconds(5));

        Assert.Equal("hi", seen!["note"]);   // string, not JsonElement
        Assert.Equal(7L, seen["count"]);     // long, not JsonElement
    }

    [Fact]
    public async Task Permanent_exception_finishes_failed_without_dead_or_retry_and_acks()
    {
        var (proc, stub, _) = Build((_, _) => throw new InvalidOperationException("permanent failure"));
        stub.Enqueue(HttpStatusCode.Created, "{\"run_id\":\"r1\"}")      // start_run
            .Enqueue(HttpStatusCode.OK, "{\"status\":\"failed\"}");       // finish_run
        var delivery = new FakeDelivery(Env().ToJsonBytes());
        var pub = new FakePublisher();

        await proc.ProcessAsync(delivery, pub, maxAttempts: 5, timeout: TimeSpan.FromSeconds(5));

        Assert.Equal(0, pub.Deads);
        Assert.Equal(0, pub.Retries);
        Assert.Contains(stub.Requests, r => r.Method == HttpMethod.Patch && r.Body.Contains("\"status\":\"failed\""));
        Assert.True(delivery.Acked);
    }
}
