namespace BrutalSystems.Jobs.Worker;

/// <summary>AsyncLocal per-run log context. Port of jobs_client.logging.bind_log_context;
/// bind run/job/tenant/owner around handler execution so log scopes carry them.</summary>
public static class LogContext
{
    private static readonly AsyncLocal<IReadOnlyDictionary<string, object?>?> _ctx = new();

    public static IReadOnlyDictionary<string, object?> Current =>
        _ctx.Value ?? new Dictionary<string, object?>();

    public static IDisposable Bind(string runId, string jobId, string tenantId, string ownerService)
    {
        var prior = _ctx.Value;
        _ctx.Value = new Dictionary<string, object?>
        {
            ["run_id"] = runId, ["job_id"] = jobId,
            ["tenant_id"] = tenantId, ["owner_service"] = ownerService,
        };
        return new Restore(prior);
    }

    private sealed class Restore(IReadOnlyDictionary<string, object?>? prior) : IDisposable
    {
        public void Dispose() => _ctx.Value = prior;
    }
}
