namespace BrutalSystems.Jobs.Core;

/// <summary>The JOBS_* env-var contract injected into spawned pods. 1:1 with
/// jobs_core.pod_env. The string VALUES are the wire contract — changing one is a
/// wire-breaking change, not a refactor.</summary>
public static class PodEnv
{
    // Identity of the run (producer always sets these)
    public const string JobId = "JOBS_JOB_ID";
    public const string RunId = "JOBS_RUN_ID";
    public const string TenantId = "JOBS_TENANT_ID";
    public const string OwnerService = "JOBS_OWNER_SERVICE";

    // Pod UID via the k8s downward API
    public const string PodUid = "POD_UID";

    // Callback + payload (producer sets conditionally)
    public const string ServiceUrl = "JOBS_SERVICE_URL";
    public const string HandlerArgs = "JOBS_HANDLER_ARGS";
    public const string HandlerCmd = "JOBS_HANDLER_CMD";

    // Read by the wrapper on scheduled/cron paths
    public const string Name = "JOBS_NAME";

    // Derived by the wrapper for the handler subprocess
    public const string ArgPrefix = "JOBS_ARG_";

    // Lifecycle event buffer
    public const string BufferPath = "JOBS_BUFFER_PATH";
    public const string BufferPathDefault = "/var/run/jobs-buffer.jsonl";

    // m2m auth key the service injects so the SDK can mint tokens back
    public const string JwtPrivateKey = "JOBS_JWT_PRIVATE_KEY";

    // W3C trace context (producer sets when tracing is active), so the wrapper can
    // parent the handler's spans under the dispatch span (server -> wrapper -> handler).
    public const string Traceparent = "JOBS_TRACEPARENT";
    public const string Tracestate = "JOBS_TRACESTATE";

    /// <summary>Map a handler-arg key to its JOBS_ARG_* env var name.</summary>
    public static string ArgEnvName(string key) => $"{ArgPrefix}{key.ToUpperInvariant()}";
}
