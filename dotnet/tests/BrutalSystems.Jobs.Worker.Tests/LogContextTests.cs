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
}
