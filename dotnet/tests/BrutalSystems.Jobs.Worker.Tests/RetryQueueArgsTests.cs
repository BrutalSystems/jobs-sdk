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
}
