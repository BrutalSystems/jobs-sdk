using System.Net;
using BrutalSystems.Jobs.Core;
using Xunit;

namespace BrutalSystems.Jobs.Client.Tests;

public class JobsClientRegisterTriggerTests
{
    private static JobsClient Make(StubHandler stub) =>
        new(new HttpClient(stub), () => "tok", "https://jobs.example.com/");

    [Fact]
    public async Task RegisterJob_posts_to_jobs_and_returns_job_id()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.OK, "{\"job_id\":\"j-1\"}");
        var id = await Make(stub).RegisterJobAsync("brokenhip.ping", null,
            new Policy { ExecutionMode = "in_process", DispatchMode = "warm-queued" });

        Assert.Equal("j-1", id);
        var req = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://jobs.example.com/api/v1/jobs", req.Url);
        Assert.Equal("Bearer tok", req.Auth);
        Assert.Contains("\"execution_mode\":\"in_process\"", req.Body);
        Assert.Contains("\"schedule\":null", req.Body);
    }

    [Fact]
    public async Task Trigger_returns_run_ids_and_hits_by_name_route()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.OK,
            "{\"run_id\":\"r-1\",\"external_ref\":\"k8s-1\",\"job_id\":\"j-1\"}");
        var res = await Make(stub).TriggerAsync("brokenhip.ping",
            args: new Dictionary<string, object?> { ["x"] = 1 });

        Assert.Equal("r-1", res.RunId);
        Assert.Equal("k8s-1", res.ExternalRef);
        Assert.Equal("https://jobs.example.com/api/v1/jobs/by-name/brokenhip.ping/trigger",
            stub.Requests[0].Url);
    }

    [Fact]
    public async Task Trigger_invokes_onMissRegister_then_retries_on_404()
    {
        var stub = new StubHandler()
            .Enqueue(HttpStatusCode.NotFound)
            .Enqueue(HttpStatusCode.OK, "{\"run_id\":\"r\",\"external_ref\":\"e\",\"job_id\":\"j\"}");
        var registered = false;
        var res = await Make(stub).TriggerAsync("brokenhip.ping",
            onMissRegister: () => { registered = true; return Task.CompletedTask; });

        Assert.True(registered);
        Assert.Equal("r", res.RunId);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task Trigger_throws_JobNotFound_when_404_and_no_register()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<JobNotFoundException>(() => Make(stub).TriggerAsync("missing"));
    }
}
