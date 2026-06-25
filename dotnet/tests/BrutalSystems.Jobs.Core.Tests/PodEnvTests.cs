using Xunit;

namespace BrutalSystems.Jobs.Core.Tests;

public class PodEnvTests
{
    [Fact]
    public void Constants_match_the_jobs_core_contract()
    {
        Assert.Equal("JOBS_JOB_ID", PodEnv.JobId);
        Assert.Equal("JOBS_RUN_ID", PodEnv.RunId);
        Assert.Equal("JOBS_TENANT_ID", PodEnv.TenantId);
        Assert.Equal("JOBS_OWNER_SERVICE", PodEnv.OwnerService);
        Assert.Equal("POD_UID", PodEnv.PodUid);
        Assert.Equal("JOBS_SERVICE_URL", PodEnv.ServiceUrl);
        Assert.Equal("JOBS_HANDLER_ARGS", PodEnv.HandlerArgs);
        Assert.Equal("JOBS_HANDLER_CMD", PodEnv.HandlerCmd);
        Assert.Equal("JOBS_NAME", PodEnv.Name);
        Assert.Equal("JOBS_ARG_", PodEnv.ArgPrefix);
        Assert.Equal("JOBS_BUFFER_PATH", PodEnv.BufferPath);
        Assert.Equal("/var/run/jobs-buffer.jsonl", PodEnv.BufferPathDefault);
        Assert.Equal("JOBS_JWT_PRIVATE_KEY", PodEnv.JwtPrivateKey);
        Assert.Equal("JOBS_TRACEPARENT", PodEnv.Traceparent);
        Assert.Equal("JOBS_TRACESTATE", PodEnv.Tracestate);
    }

    [Fact]
    public void ArgEnvName_uppercases_with_prefix()
        => Assert.Equal("JOBS_ARG_LEAD_ID", PodEnv.ArgEnvName("lead_id"));
}
