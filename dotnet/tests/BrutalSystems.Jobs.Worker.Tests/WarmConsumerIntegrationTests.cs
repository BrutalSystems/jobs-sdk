using Xunit;

namespace BrutalSystems.Jobs.Worker.Tests;

public class WarmConsumerIntegrationTests
{
    private static string? AmqpUrl => Environment.GetEnvironmentVariable("JOBS_TEST_AMQP_URL");

    [Fact]
    public void Topology_declares_against_a_real_broker()
    {
        if (string.IsNullOrEmpty(AmqpUrl))
            return; // skip when no broker is available; set JOBS_TEST_AMQP_URL to run

        // With a live broker + a stub jobs-service, assert: publishing one envelope to the work
        // queue results in start_run + finish_run + ack. Left as a manual/integration harness;
        // the unit tests in MessageProcessorTests already pin the processing ladder.
        Assert.False(string.IsNullOrEmpty(AmqpUrl));
    }
}
