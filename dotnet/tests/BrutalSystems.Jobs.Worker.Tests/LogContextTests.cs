using Xunit;

namespace BrutalSystems.Jobs.Worker.Tests;

public class LogContextTests
{
    [Fact]
    public void Bind_sets_then_restores_on_dispose()
    {
        Assert.Empty(LogContext.Current);
        using (LogContext.Bind("r1", "j1", "_org", "brokenhip-be"))
        {
            Assert.Equal("r1", LogContext.Current["run_id"]);
            Assert.Equal("brokenhip-be", LogContext.Current["owner_service"]);
        }
        Assert.Empty(LogContext.Current);
    }

    [Fact]
    public void Nested_bind_restores_outer_values_on_dispose()
    {
        using (LogContext.Bind("r-outer", "j-outer", "t-outer", "o-outer"))
        {
            Assert.Equal("r-outer", LogContext.Current["run_id"]);

            using (LogContext.Bind("r-inner", "j-inner", "t-inner", "o-inner"))
            {
                Assert.Equal("r-inner", LogContext.Current["run_id"]);
            }

            Assert.Equal("r-outer", LogContext.Current["run_id"]);
        }

        Assert.Empty(LogContext.Current);
    }

    [Fact]
    public async Task Context_flows_across_await()
    {
        using (LogContext.Bind("r-a", "j-a", "t-a", "o-a"))
        {
            await Task.Yield();
            Assert.Equal("r-a", LogContext.Current["run_id"]);
        }
    }
}
