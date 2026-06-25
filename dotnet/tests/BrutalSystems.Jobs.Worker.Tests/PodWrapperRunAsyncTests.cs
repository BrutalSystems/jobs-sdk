using System.Net;
using System.Text.Json;
using BrutalSystems.Jobs.Client;
using BrutalSystems.Jobs.Core;
using Xunit;

namespace BrutalSystems.Jobs.Worker.Tests;

// These tests mutate process-global JOBS_* env vars.  Serialise them so concurrent xUnit classes
// don't race on the same environment.
[CollectionDefinition("PodWrapperEnv", DisableParallelization = true)]
public sealed class PodWrapperEnvCollection;

[Collection("PodWrapperEnv")]
public class PodWrapperRunAsyncTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static JobsClient MakeClient(StubHandler stub) =>
        new(new HttpClient(stub), () => "tok", "https://jobs.example.com");

    /// <summary>Set the minimal JOBS_* env vars that RunAsync requires, returning the set of keys
    /// set so they can be cleared in a finally block.</summary>
    private static void SetRequiredEnv(string jobId = "j-test")
    {
        Environment.SetEnvironmentVariable(PodEnv.OwnerService, "brokenhip-be");
        Environment.SetEnvironmentVariable(PodEnv.TenantId, "_org");
        Environment.SetEnvironmentVariable(PodEnv.JobId, jobId);
        Environment.SetEnvironmentVariable(PodEnv.HandlerCmd, "[\"echo\",\"hello\"]");
        Environment.SetEnvironmentVariable(PodEnv.HandlerArgs, "{}");
        Environment.SetEnvironmentVariable(PodEnv.PodUid, "pod-uid-test");
    }

    private static void ClearRequiredEnv()
    {
        foreach (var key in new[]
        {
            PodEnv.OwnerService, PodEnv.TenantId, PodEnv.JobId, PodEnv.RunId,
            PodEnv.HandlerCmd, PodEnv.HandlerArgs, PodEnv.PodUid, PodEnv.Name,
            PodEnv.BufferPath,
        })
            Environment.SetEnvironmentVariable(key, null);
    }

    // -----------------------------------------------------------------------
    // Exit code 0  →  finish_run with "succeeded"
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExitCode0_calls_finish_run_with_succeeded()
    {
        SetRequiredEnv();
        try
        {
            var stub = new StubHandler()
                .Enqueue(HttpStatusCode.Created, "{\"run_id\":\"r-1\"}")   // start_run
                .Enqueue(HttpStatusCode.OK, "{\"status\":\"succeeded\"}"); // finish_run

            var client = MakeClient(stub);

            // Inject a fake runner that returns exit code 0 and no summary.
            ProcessRunner fakeRunner = (_, _, _, _, _) => Task.FromResult(new ProcessResult(0, null));

            var exitCode = await PodWrapper.RunAsync(client, CancellationToken.None, runner: fakeRunner);

            Assert.Equal(0, exitCode);

            // Verify start_run was POSTed.
            var startReq = Assert.Single(stub.Requests, r => r.Url.EndsWith("/runs") && r.Method == HttpMethod.Post);
            Assert.Contains("\"trigger\":\"schedule\"", startReq.Body);

            // Verify finish_run was PATCHed with succeeded and exit_code 0.
            var finishReq = Assert.Single(stub.Requests, r => r.Method == HttpMethod.Patch);
            Assert.Contains("\"status\":\"succeeded\"", finishReq.Body);
            Assert.Contains("\"exit_code\":0", finishReq.Body);
        }
        finally { ClearRequiredEnv(); }
    }

    // -----------------------------------------------------------------------
    // Exit code 3  →  finish_run with "failed" + exit_code 3
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExitCode3_calls_finish_run_with_failed_and_exit_code_3()
    {
        SetRequiredEnv();
        try
        {
            var stub = new StubHandler()
                .Enqueue(HttpStatusCode.Created, "{\"run_id\":\"r-2\"}")  // start_run
                .Enqueue(HttpStatusCode.OK, "{\"status\":\"failed\"}");   // finish_run

            var client = MakeClient(stub);

            ProcessRunner fakeRunner = (_, _, _, _, _) => Task.FromResult(new ProcessResult(3, null));

            var exitCode = await PodWrapper.RunAsync(client, CancellationToken.None, runner: fakeRunner);

            Assert.Equal(3, exitCode);

            var finishReq = Assert.Single(stub.Requests, r => r.Method == HttpMethod.Patch);
            Assert.Contains("\"status\":\"failed\"", finishReq.Body);
            Assert.Contains("\"exit_code\":3", finishReq.Body);
        }
        finally { ClearRequiredEnv(); }
    }

    // -----------------------------------------------------------------------
    // start_run failure → EventBuffer fallback path is exercised
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StartRun_failure_writes_to_event_buffer_and_throws()
    {
        SetRequiredEnv();

        // Direct the buffer to a temp file so we can inspect it and avoid the default
        // /var/run path which may not be writable in CI.
        var bufPath = Path.Combine(Path.GetTempPath(), $"jobs-buf-test-{Guid.NewGuid():N}.jsonl");
        Environment.SetEnvironmentVariable(PodEnv.BufferPath, bufPath);
        try
        {
            // Return a non-success status so start_run throws.
            var stub = new StubHandler()
                .Enqueue(HttpStatusCode.ServiceUnavailable, "{\"error\":\"down\"}");

            var client = MakeClient(stub);

            // The runner should never be called; use a sentinel that throws if it is.
            ProcessRunner shouldNotRun = (_, _, _, _, _) => throw new InvalidOperationException("runner must not be called");

            await Assert.ThrowsAsync<HttpRequestException>(
                () => PodWrapper.RunAsync(client, CancellationToken.None, runner: shouldNotRun));

            // The buffer file must have been written with a start_run event.
            Assert.True(File.Exists(bufPath), "EventBuffer file must exist after start_run failure");
            var lines = File.ReadAllLines(bufPath);
            Assert.NotEmpty(lines);
            var doc = JsonDocument.Parse(lines[0]);
            Assert.Equal("start_run", doc.RootElement.GetProperty("kind").GetString());
            Assert.Equal("j-test", doc.RootElement.GetProperty("job_id").GetString());
        }
        finally
        {
            ClearRequiredEnv();
            if (File.Exists(bufPath)) File.Delete(bufPath);
        }
    }

    // -----------------------------------------------------------------------
    // Summary flows to finish_run — pins the regression
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Summary_from_runner_flows_to_finish_run()
    {
        SetRequiredEnv();
        try
        {
            var stub = new StubHandler()
                .Enqueue(HttpStatusCode.Created, "{\"run_id\":\"r-summary\"}")  // start_run
                .Enqueue(HttpStatusCode.OK, "{\"status\":\"succeeded\"}");      // finish_run

            var client = MakeClient(stub);

            // Fake runner returns exit 0 with a non-null summary dict.
            IReadOnlyDictionary<string, object?> fakeSummary = new Dictionary<string, object?>
            {
                ["rows"] = (object?)5,
            };
            ProcessRunner fakeRunner = (_, _, _, _, _) =>
                Task.FromResult(new ProcessResult(0, fakeSummary));

            var exitCode = await PodWrapper.RunAsync(client, CancellationToken.None, runner: fakeRunner);

            Assert.Equal(0, exitCode);

            // finish_run body must carry the summary payload.
            var finishReq = Assert.Single(stub.Requests, r => r.Method == HttpMethod.Patch);
            Assert.Contains("\"summary\"", finishReq.Body);
            Assert.Contains("\"rows\"", finishReq.Body);
        }
        finally { ClearRequiredEnv(); }
    }

    // -----------------------------------------------------------------------
    // Stdout lines are streamed to LogAsync via onStdoutLine callback
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StdoutLines_are_streamed_to_LogAsync()
    {
        SetRequiredEnv();
        try
        {
            var stub = new StubHandler()
                .Enqueue(HttpStatusCode.Created, "{\"run_id\":\"r-log\"}")   // start_run
                .Enqueue(HttpStatusCode.OK, "{}")                             // LogAsync POST (best-effort, swallowed)
                .Enqueue(HttpStatusCode.OK, "{\"status\":\"succeeded\"}");   // finish_run

            var client = MakeClient(stub);

            // Fake runner invokes onStdoutLine with a test line before returning.
            ProcessRunner fakeRunner = async (_, _, _, onStdoutLine, _) =>
            {
                await onStdoutLine("hello from handler");
                return new ProcessResult(0, null);
            };

            var exitCode = await PodWrapper.RunAsync(client, CancellationToken.None, runner: fakeRunner);

            Assert.Equal(0, exitCode);

            // A POST to the /logs endpoint must have been captured with the line content.
            var logReq = Assert.Single(stub.Requests,
                r => r.Method == HttpMethod.Post && r.Url.EndsWith("/logs"));
            Assert.Contains("hello from handler", logReq.Body);
        }
        finally { ClearRequiredEnv(); }
    }

    // -----------------------------------------------------------------------
    // Trace context is forwarded into the handler subprocess env
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Forwards_JOBS_TRACEPARENT_into_handler_env()
    {
        SetRequiredEnv();
        Environment.SetEnvironmentVariable(PodEnv.Traceparent, "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        try
        {
            var stub = new StubHandler()
                .Enqueue(HttpStatusCode.Created, "{\"run_id\":\"r-tp\"}")
                .Enqueue(HttpStatusCode.OK, "{\"status\":\"succeeded\"}");
            var client = MakeClient(stub);

            IReadOnlyDictionary<string, string>? captured = null;
            ProcessRunner capturingRunner = (_, _, argEnv, _, _) =>
            {
                captured = argEnv;
                return Task.FromResult(new ProcessResult(0, null));
            };

            await PodWrapper.RunAsync(client, CancellationToken.None, runner: capturingRunner);

            Assert.NotNull(captured);
            Assert.Equal(
                "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
                captured![PodEnv.Traceparent]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PodEnv.Traceparent, null);
            ClearRequiredEnv();
        }
    }

    // -----------------------------------------------------------------------
    // (Optional) Real-subprocess smoke test: exit 3 → failed
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RealRunner_exit3_maps_to_failed_on_posix()
    {
        if (OperatingSystem.IsWindows()) return;

        SetRequiredEnv();
        // Override the handler cmd to something that exits 3.
        Environment.SetEnvironmentVariable(PodEnv.HandlerCmd, "[\"/bin/sh\",\"-c\",\"exit 3\"]");

        try
        {
            var stub = new StubHandler()
                .Enqueue(HttpStatusCode.Created, "{\"run_id\":\"r-smoke\"}")
                .Enqueue(HttpStatusCode.OK, "{\"status\":\"failed\"}");

            var client = MakeClient(stub);

            var exitCode = await PodWrapper.RunAsync(client);   // uses RealProcessRunner

            Assert.Equal(3, exitCode);

            var finishReq = Assert.Single(stub.Requests, r => r.Method == HttpMethod.Patch);
            Assert.Contains("\"status\":\"failed\"", finishReq.Body);
            Assert.Contains("\"exit_code\":3", finishReq.Body);
        }
        finally { ClearRequiredEnv(); }
    }
}
