using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace BrutalSystems.Jobs.Client.Tests;

// The FromEnv tests mutate the process-global JOBS_SERVICE_URL; xUnit parallelizes across classes,
// so this collection serializes them against the rest of the suite to avoid a flaky env-var race.
[CollectionDefinition("JobsClientEnv", DisableParallelization = true)]
public sealed class JobsClientEnvCollection;

[Collection("JobsClientEnv")]
public class JobsClientReadsTests
{
    private static JobsClient Make(StubHandler stub) =>
        new(new HttpClient(stub), () => "tok", "https://jobs.example.com");

    [Fact]
    public async Task ListJobsFiltered_passes_query_params()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.OK, "[{\"name\":\"a\"}]");
        var jobs = await Make(stub).ListJobsFilteredAsync(ownerService: "brokenhip-be", dispatchMode: "warm-queued");
        Assert.Single(jobs);
        Assert.Contains("owner_service=brokenhip-be", stub.Requests[0].Url);
        Assert.Contains("dispatch_mode=warm-queued", stub.Requests[0].Url);
    }

    [Fact]
    public async Task GetJobByName_throws_on_404()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<JobNotFoundException>(() => Make(stub).GetJobByNameAsync("brokenhip-be", "missing"));
    }

    [Fact]
    public async Task GetRunByExternalRef_throws_RunNotFoundException_on_404()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<RunNotFoundException>(() => Make(stub).GetRunByExternalRefAsync("some-ref"));
    }

    [Fact]
    public async Task GetRunByExternalRef_returns_json_and_sends_correct_request()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.OK, "{\"id\":\"run-1\",\"external_ref\":\"abc-123\"}");
        var result = await Make(stub).GetRunByExternalRefAsync("abc-123");
        Assert.Equal("run-1", result.GetProperty("id").GetString());
        Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Get, stub.Requests[0].Method);
        Assert.EndsWith("/api/v1/runs/by-external-ref/abc-123", stub.Requests[0].Url);
    }

    [Fact]
    public async Task ListJobsFiltered_parses_empty_list()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.OK, "[]");
        var jobs = await Make(stub).ListJobsFilteredAsync();
        Assert.Empty(jobs);
    }

    [Fact]
    public void FromEnv_requires_service_url()
    {
        var prior = Environment.GetEnvironmentVariable("JOBS_SERVICE_URL");
        Environment.SetEnvironmentVariable("JOBS_SERVICE_URL", null);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                JobsClient.FromEnv(new HttpClient(), ownerService: "svc", signingKey: RSA.Create(2048)));
        }
        finally { Environment.SetEnvironmentVariable("JOBS_SERVICE_URL", prior); }
    }

    [Fact]
    public void FromEnv_builds_client_from_env_and_key()
    {
        var prior = Environment.GetEnvironmentVariable("JOBS_SERVICE_URL");
        Environment.SetEnvironmentVariable("JOBS_SERVICE_URL", "https://jobs.example.com");
        try
        {
            var client = JobsClient.FromEnv(new HttpClient(), ownerService: "brokenhip-be",
                tenantId: "_org", signingKey: RSA.Create(2048));
            Assert.NotNull(client);
        }
        finally { Environment.SetEnvironmentVariable("JOBS_SERVICE_URL", prior); }
    }
}
