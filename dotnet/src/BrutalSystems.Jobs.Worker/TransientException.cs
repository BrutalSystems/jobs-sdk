namespace BrutalSystems.Jobs.Worker;

/// <summary>Raise from a warm-queued handler to trigger an AMQP-level retry. Any other
/// exception is terminal (finish_run(failed) immediately). Port of jobs_client.exceptions.TransientError.</summary>
public sealed class TransientException(string message) : Exception(message);
