namespace BrutalSystems.Jobs.Core;

/// <summary>AMQP exchange + queue-name helpers for the `jobs` exchange. 1:1 with
/// jobs_core.envelope so the SDK and service can never disagree on topology.</summary>
public static class QueueNames
{
    public const string ExchangeName = "jobs";

    public static string Work(string ownerService, string jobName, string dispatchMode)
    {
        var baseName = $"jobs.{ownerService}.{jobName}";
        return dispatchMode == "cold-queued" ? $"{baseName}.cold" : baseName;
    }

    public static string Retry(string workQueue) => $"{StripCold(workQueue)}.retry";

    public static string Dead(string workQueue) => $"{StripCold(workQueue)}.dead";

    private static string StripCold(string workQueue) =>
        workQueue.EndsWith(".cold", StringComparison.Ordinal)
            ? workQueue[..^".cold".Length]
            : workQueue;
}
