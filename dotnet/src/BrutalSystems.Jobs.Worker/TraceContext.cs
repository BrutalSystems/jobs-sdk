using System.Diagnostics;
using BrutalSystems.Jobs.Core;

namespace BrutalSystems.Jobs.Worker;

/// <summary>W3C trace-context propagation across the wrapper -> handler subprocess
/// boundary. Port of jobs_client._tracing.
///
/// Uses the BCL <see cref="Activity"/> primitive (the same one OpenTelemetry exports),
/// so there is NO OpenTelemetry dependency — handlers run fine without it. The service
/// injects the dispatch context as <c>JOBS_TRACEPARENT</c>; the wrapper forwards it to
/// the handler subprocess, and the handler calls <see cref="AdoptTraceContext"/> at
/// startup so its spans nest under the run/dispatch span instead of starting a new
/// root trace.</summary>
public static class TraceContext
{
    /// <summary>ActivitySource name for the wrapper/handler boundary span. Handlers that
    /// configure OpenTelemetry should add this source (e.g. <c>AddSource(TraceContext.SourceName)</c>)
    /// so the <c>jobs.handler</c> span itself is exported and the trace shows a clean
    /// server -> handler edge. Not required for correlation — the handler's own spans carry
    /// the right trace id regardless.</summary>
    public const string SourceName = "BrutalSystems.Jobs.Worker";

    private static readonly ActivitySource Source = new(SourceName);

    /// <summary>Env vars carrying the active trace context to forward to the handler
    /// subprocess. Prefers the wrapper's current <see cref="Activity"/> (when the wrapper
    /// itself is traced) and falls back to the incoming <c>JOBS_TRACEPARENT</c> the service
    /// injected. Empty when there is no context at all.</summary>
    public static IReadOnlyDictionary<string, string> HandlerEnv()
    {
        var current = Activity.Current;
        var traceparent = current?.Id ?? Environment.GetEnvironmentVariable(PodEnv.Traceparent);
        var tracestate = current?.TraceStateString ?? Environment.GetEnvironmentVariable(PodEnv.Tracestate);

        var env = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(traceparent)) env[PodEnv.Traceparent] = traceparent;
        if (!string.IsNullOrEmpty(tracestate)) env[PodEnv.Tracestate] = tracestate;
        return env;
    }

    /// <summary>Adopt the dispatch trace context the wrapper forwarded as <c>JOBS_TRACEPARENT</c>,
    /// so this handler's spans nest under the run/dispatch span instead of starting a new root
    /// trace. Call ONCE at handler startup, before any span is created.
    ///
    /// Sets <see cref="Activity.Current"/> to a <c>jobs.handler</c> span whose parent is the
    /// forwarded context, so subsequent spans nest under it and carry the dispatch trace id.
    /// Prefers an exported span from <see cref="SourceName"/> (when the handler's OTel config
    /// adds that source); otherwise falls back to a plain Activity so the trace id still
    /// propagates. Safe no-op when nothing was forwarded or the value is malformed. Returns the
    /// started Activity (dispose/stop it when the handler finishes), or null.</summary>
    public static Activity? AdoptTraceContext()
    {
        var traceparent = Environment.GetEnvironmentVariable(PodEnv.Traceparent);
        if (string.IsNullOrEmpty(traceparent)) return null;

        var tracestate = Environment.GetEnvironmentVariable(PodEnv.Tracestate);
        if (!ActivityContext.TryParse(traceparent, tracestate, out var parent)) return null;

        // Exported span when the handler listens to our source; else a plain Activity that
        // still sets Activity.Current so the handler's own spans share the trace.
        var activity = Source.StartActivity("jobs.handler", ActivityKind.Internal, parent);
        if (activity is null)
        {
            activity = new Activity("jobs.handler");
            activity.SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags);
            if (!string.IsNullOrEmpty(parent.TraceState)) activity.TraceStateString = parent.TraceState;
            activity.Start();
        }
        return activity;
    }
}
