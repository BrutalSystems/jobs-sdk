using Xunit;

namespace BrutalSystems.Jobs.Worker.Tests;

public class HandlerRegistryTests
{
    private static JobHandler Noop => (_, _) => Task.FromResult<IReadOnlyDictionary<string, object?>?>(null);

    [Fact]
    public void Register_then_TryGet_returns_handler()
    {
        var reg = new HandlerRegistry();
        reg.Register("brokenhip.ping", Noop);
        Assert.True(reg.TryGet("brokenhip.ping", out var h));
        Assert.NotNull(h);
        Assert.True(reg.Contains("brokenhip.ping"));
    }

    [Fact]
    public void Duplicate_registration_throws()
    {
        var reg = new HandlerRegistry();
        reg.Register("x", Noop);
        Assert.Throws<InvalidOperationException>(() => reg.Register("x", Noop));
    }

    [Fact]
    public void TryGet_missing_returns_false()
        => Assert.False(new HandlerRegistry().TryGet("nope", out _));
}
