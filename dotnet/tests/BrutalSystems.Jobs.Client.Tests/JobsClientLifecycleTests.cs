using System.Net;
using Xunit;

namespace BrutalSystems.Jobs.Client.Tests;

public class JobsClientLifecycleTests
{
    private static JobsClient Make(StubHandler stub) =>
        new(new HttpClient(stub), () => "tok", "https://jobs.example.com");

    [Fact]
    public async Task StartRun_posts_runs_and_returns_run_id()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.Created, "{\"run_id\":\"r-9\"}");
        var id = await Make(stub).StartRunAsync("j-1", idempotencyKey: "pod-uid", trigger: "queue");
        Assert.Equal("r-9", id);
        var req = stub.Requests[0];
        Assert.Equal("https://jobs.example.com/api/v1/jobs/j-1/runs", req.Url);
        Assert.Contains("\"trigger\":\"queue\"", req.Body);
        Assert.Contains("\"idempotency_key\":\"pod-uid\"", req.Body);
    }

    [Fact]
    public async Task FinishRun_patches_and_returns_status()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.OK, "{\"status\":\"succeeded\"}");
        var status = await Make(stub).FinishRunAsync("j-1", "r-1", "succeeded", 0,
            new Dictionary<string, object?> { ["ok"] = true });
        Assert.Equal("succeeded", status);
        Assert.Equal(HttpMethod.Patch, stub.Requests[0].Method);
        Assert.Equal("https://jobs.example.com/api/v1/jobs/j-1/runs/r-1", stub.Requests[0].Url);
        Assert.Contains("\"exit_code\":0", stub.Requests[0].Body);
        Assert.Contains("\"status\":\"succeeded\"", stub.Requests[0].Body);
    }

    [Fact]
    public async Task Heartbeat_posts_to_heartbeat_endpoint()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.OK);
        await Make(stub).HeartbeatAsync("j-42");
        var req = stub.Requests[0];
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://jobs.example.com/api/v1/jobs/j-42/heartbeat", req.Url);
        Assert.Contains("Bearer", req.Auth ?? "");
    }

    [Fact]
    public async Task Log_never_throws_even_on_500()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.InternalServerError);
        await Make(stub).LogAsync("j-5", "r-7", "a log line");   // must not throw
        Assert.Single(stub.Requests);
        Assert.Equal("https://jobs.example.com/api/v1/jobs/j-5/runs/r-7/logs", stub.Requests[0].Url);
        Assert.Contains("\"line\":\"a log line\"", stub.Requests[0].Body);
    }
}
