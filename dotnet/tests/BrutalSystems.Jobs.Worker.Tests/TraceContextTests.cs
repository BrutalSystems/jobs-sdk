using System.Diagnostics;
using BrutalSystems.Jobs.Core;
using Xunit;

namespace BrutalSystems.Jobs.Worker.Tests;

// Mutates the process-global JOBS_TRACEPARENT env + Activity.Current; serialise with the
// other env-mutating wrapper tests.
[Collection("PodWrapperEnv")]
public class TraceContextTests
{
    private const string TraceId = "0af7651916cd43dd8448eb211c80319c";
    private const string ParentSpan = "b7ad6b7169203331";
    private const string Incoming = $"00-{TraceId}-{ParentSpan}-01";

    private static void ClearTraceEnv()
    {
        Environment.SetEnvironmentVariable(PodEnv.Traceparent, null);
        Environment.SetEnvironmentVariable(PodEnv.Tracestate, null);
    }

    [Fact]
    public void AdoptTraceContext_nests_under_forwarded_trace()
    {
        Environment.SetEnvironmentVariable(PodEnv.Traceparent, Incoming);
        try
        {
            using var activity = TraceContext.AdoptTraceContext();
            Assert.NotNull(activity);
            Assert.Same(activity, Activity.Current);
            // Same trace as the dispatch span; parented on its span id (a new child span id).
            Assert.Equal(TraceId, activity!.TraceId.ToHexString());
            Assert.Equal(ParentSpan, activity.ParentSpanId.ToHexString());
        }
        finally { ClearTraceEnv(); }
    }

    [Fact]
    public void AdoptTraceContext_emits_exported_span_when_source_is_listened()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == TraceContext.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        Environment.SetEnvironmentVariable(PodEnv.Traceparent, Incoming);
        try
        {
            using var activity = TraceContext.AdoptTraceContext();
            Assert.NotNull(activity);
            Assert.Equal(TraceContext.SourceName, activity!.Source.Name);  // came from our ActivitySource
            Assert.Equal("jobs.handler", activity.OperationName);
            Assert.Equal(TraceId, activity.TraceId.ToHexString());
        }
        finally { ClearTraceEnv(); }
    }

    [Fact]
    public void AdoptTraceContext_is_noop_without_env() {
        ClearTraceEnv();
        Assert.Null(TraceContext.AdoptTraceContext());
    }

    [Fact]
    public void AdoptTraceContext_is_noop_on_malformed_traceparent()
    {
        Environment.SetEnvironmentVariable(PodEnv.Traceparent, "not-a-traceparent");
        try { Assert.Null(TraceContext.AdoptTraceContext()); }
        finally { ClearTraceEnv(); }
    }

    [Fact]
    public void HandlerEnv_forwards_incoming_context_when_wrapper_untraced()
    {
        Environment.SetEnvironmentVariable(PodEnv.Traceparent, Incoming);
        Environment.SetEnvironmentVariable(PodEnv.Tracestate, "vendor=x");
        try
        {
            var env = TraceContext.HandlerEnv();
            Assert.Equal(Incoming, env[PodEnv.Traceparent]);
            Assert.Equal("vendor=x", env[PodEnv.Tracestate]);
        }
        finally { ClearTraceEnv(); }
    }

    [Fact]
    public void HandlerEnv_is_empty_when_no_context()
    {
        ClearTraceEnv();
        Assert.Empty(TraceContext.HandlerEnv());
    }
}
