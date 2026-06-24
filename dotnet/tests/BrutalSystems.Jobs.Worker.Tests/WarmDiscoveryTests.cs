using System.Net;
using System.Text.Json;
using BrutalSystems.Jobs.Client;
using Xunit;

namespace BrutalSystems.Jobs.Worker.Tests;

public class WarmDiscoveryTests
{
    private static JobsClient Make(StubHandler stub) =>
        new(new HttpClient(stub), () => "tok", "https://jobs.example.com");

    [Fact]
    public async Task Discovery_retries_on_503_then_succeeds()
    {
        var stub = new StubHandler()
            .Enqueue(HttpStatusCode.ServiceUnavailable)
            .Enqueue(HttpStatusCode.OK, "[{\"name\":\"brokenhip.ping\"}]");
        var jobs = await WarmDiscovery.DiscoverWarmJobsAsync(Make(stub), "brokenhip-be",
            delay: _ => Task.CompletedTask);
        Assert.Single(jobs);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task Discovery_does_not_retry_on_400()
    {
        var stub = new StubHandler().Enqueue(HttpStatusCode.BadRequest);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            WarmDiscovery.DiscoverWarmJobsAsync(Make(stub), "brokenhip-be", delay: _ => Task.CompletedTask));
        Assert.Single(stub.Requests);
    }

    [Fact]
    public void Validate_throws_when_a_job_has_no_handler()
    {
        var jobs = JsonSerializer.Deserialize<List<JsonElement>>("[{\"name\":\"brokenhip.ping\"}]")!;
        var ex = Record.Exception(() => WarmDiscovery.ValidateHandlers(jobs, new HandlerRegistry()));
        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public void Validate_passes_when_handler_present()
    {
        var jobs = JsonSerializer.Deserialize<List<JsonElement>>("[{\"name\":\"brokenhip.ping\"}]")!;
        var reg = new HandlerRegistry();
        reg.Register("brokenhip.ping", (_, _) => Task.FromResult<IReadOnlyDictionary<string, object?>?>(null));
        WarmDiscovery.ValidateHandlers(jobs, reg);   // no throw
    }
}
