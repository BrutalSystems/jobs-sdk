using Xunit;

namespace BrutalSystems.Jobs.Core.Tests;

public class QueueNamesTests
{
    [Fact]
    public void Exchange_name_is_jobs() => Assert.Equal("jobs", QueueNames.ExchangeName);

    [Fact]
    public void Work_warm_has_no_suffix()
        => Assert.Equal("jobs.my-api.score-lead", QueueNames.Work("my-api", "score-lead", "warm-queued"));

    [Fact]
    public void Work_cold_has_cold_suffix()
        => Assert.Equal("jobs.my-api.score-lead.cold", QueueNames.Work("my-api", "score-lead", "cold-queued"));

    [Theory]
    [InlineData("jobs.my-api.score-lead", "jobs.my-api.score-lead.retry")]
    [InlineData("jobs.my-api.score-lead.cold", "jobs.my-api.score-lead.retry")]
    public void Retry_strips_cold_then_appends(string work, string expected)
        => Assert.Equal(expected, QueueNames.Retry(work));

    [Theory]
    [InlineData("jobs.my-api.score-lead", "jobs.my-api.score-lead.dead")]
    [InlineData("jobs.my-api.score-lead.cold", "jobs.my-api.score-lead.dead")]
    public void Dead_strips_cold_then_appends(string work, string expected)
        => Assert.Equal(expected, QueueNames.Dead(work));
}
