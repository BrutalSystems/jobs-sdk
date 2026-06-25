using System.Diagnostics;
using System.Text.Json;
using BrutalSystems.Jobs.Client;
using BrutalSystems.Jobs.Core;

namespace BrutalSystems.Jobs.Worker;

/// <summary>Result returned by a <see cref="ProcessRunner"/> invocation.</summary>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="Summary">Parsed summary dict from a <c>__JOBS_SUMMARY__</c> line, or null.</param>
internal record ProcessResult(int ExitCode, IReadOnlyDictionary<string, object?>? Summary);

/// <summary>Delegate seam for the subprocess execution step. Receives the executable path, per-handler
/// flags, per-handler env vars, a per-stdout-line callback, and returns a <see cref="ProcessResult"/>.
/// The real implementation is <see cref="PodWrapper.RealProcessRunner"/>.</summary>
internal delegate Task<ProcessResult> ProcessRunner(
    string[] cmdArgv,
    IReadOnlyList<string> handlerFlags,
    IReadOnlyDictionary<string, string> argEnv,
    Func<string, Task> onStdoutLine,
    CancellationToken ct);

/// <summary>Pod entrypoint that wraps a handler subprocess with jobs-service lifecycle calls.
/// Port of jobs_client.wrapper. Invoke RunAsync from a console <c>Main</c> in the pod image.</summary>
public static class PodWrapper
{
    private const string SummaryMarker = "__JOBS_SUMMARY__";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Converts handler args to JOBS_ARG_* env var pairs (skips "handler").</summary>
    public static IReadOnlyDictionary<string, string> ArgsToEnv(IReadOnlyDictionary<string, object?> args)
    {
        var env = new Dictionary<string, string>();
        foreach (var (k, v) in args)
            if (k != "handler")
                env[PodEnv.ArgEnvName(k)] = v?.ToString() ?? "";
        return env;
    }

    /// <summary>Converts handler args to --kebab-key value CLI flags (skips "handler"; bare flag for true; skips false).</summary>
    public static IReadOnlyList<string> ArgsToFlags(IReadOnlyDictionary<string, object?> args)
    {
        var flags = new List<string>();
        foreach (var (k, v) in args)
        {
            if (k == "handler") continue;
            var flag = "--" + k.Replace('_', '-');
            if (v is bool b) { if (b) flags.Add(flag); continue; }
            flags.Add(flag);
            flags.Add(v?.ToString() ?? "");
        }
        return flags;
    }

    /// <summary>Parses a <c>__JOBS_SUMMARY__</c>-prefixed JSON line; returns null if absent or malformed.</summary>
    public static IReadOnlyDictionary<string, object?>? ExtractSummary(string line)
    {
        if (!line.StartsWith(SummaryMarker, StringComparison.Ordinal)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(
                line[SummaryMarker.Length..].Trim(), Json);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Real subprocess runner: launches the process, drains stdout/stderr, waits, and
    /// returns the exit code + any extracted summary. Kills the process tree on cancellation. Stderr
    /// is drained concurrently and always awaited in a finally block.</summary>
    internal static async Task<ProcessResult> RealProcessRunner(
        string[] cmdArgv,
        IReadOnlyList<string> handlerFlags,
        IReadOnlyDictionary<string, string> argEnv,
        Func<string, Task> onStdoutLine,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cmdArgv[0],
            RedirectStandardOutput = true,
            // Drain stderr to avoid the classic stdout+stderr pipe deadlock: if both pipes fill
            // and nothing reads them, WaitForExitAsync hangs forever. We redirect and consume it.
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in cmdArgv.Skip(1)) psi.ArgumentList.Add(a);
        foreach (var f in handlerFlags) psi.ArgumentList.Add(f);
        foreach (var (k, v) in argEnv) psi.Environment[k] = v;

        using var proc = Process.Start(psi)!;
        IReadOnlyDictionary<string, object?>? summary = null;

        // Drain stderr concurrently; pass ct so it stops promptly on cancellation.
        // Wrap the drain body so failures are non-fatal (IO/cancellation swallowed inside).
        var stderrDrain = Task.Run(async () =>
        {
            try
            {
                string? errLine;
                while ((errLine = await proc.StandardError.ReadLineAsync(ct)) is not null)
                    Console.Error.WriteLine(errLine);
            }
            catch (OperationCanceledException) { /* drain cancelled — expected */ }
            catch (Exception) { /* non-fatal: stderr drain failure must not abort the wrapper */ }
        }, ct);

        try
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                Console.WriteLine(line);
                await onStdoutLine(line);
                summary = ExtractSummary(line) ?? summary;
            }

            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Kill the child process tree so we don't leave orphans.
            try { proc.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw;
        }
        finally
        {
            // Always observe the drain task so its exception (if any) doesn't become unobserved.
            await stderrDrain.ConfigureAwait(false);
        }

        return new ProcessResult(proc.ExitCode, summary);
    }

    /// <summary>Full pod lifecycle. Reads JOBS_* env, resolves the job id, starts the run, spawns the
    /// handler subprocess, streams stdout to logs + summary marker, finishes the run, buffering on
    /// service errors. Stderr is drained concurrently to avoid the classic stdout/stderr pipe deadlock.</summary>
    public static Task<int> RunAsync(JobsClient client, CancellationToken ct = default)
        => RunAsync(client, ct, runner: null);

    /// <summary>Internal overload with an injectable <paramref name="runner"/> seam for unit tests.
    /// Pass null to use the real subprocess implementation (<see cref="RealProcessRunner"/>).</summary>
    internal static async Task<int> RunAsync(JobsClient client, CancellationToken ct,
        ProcessRunner? runner)
    {
        runner ??= RealProcessRunner;

        string Required(string name) => Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"{name} env var required by wrapper");

        var ownerService = Required(PodEnv.OwnerService);
        _ = Required(PodEnv.TenantId);
        var handlerCmdRaw = Required(PodEnv.HandlerCmd);
        var jobIdEnv = Environment.GetEnvironmentVariable(PodEnv.JobId);
        var runIdEnv = Environment.GetEnvironmentVariable(PodEnv.RunId);
        var name = Environment.GetEnvironmentVariable(PodEnv.Name);
        var argsRaw = Environment.GetEnvironmentVariable(PodEnv.HandlerArgs) ?? "{}";

        var handlerArgs = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsRaw, Json) ?? new();
        var cmdArgv = JsonSerializer.Deserialize<List<string>>(handlerCmdRaw, Json)
            ?? throw new InvalidOperationException("JOBS_HANDLER_CMD must be a JSON list of strings");
        if (cmdArgv.Count == 0) throw new InvalidOperationException("JOBS_HANDLER_CMD must be non-empty");

        var buffer = new EventBuffer();

        var jobId = jobIdEnv;
        if (string.IsNullOrEmpty(jobId))
        {
            if (string.IsNullOrEmpty(name))
                throw new InvalidOperationException("either JOBS_JOB_ID or JOBS_NAME env var required by wrapper");
            var job = await client.GetJobByNameAsync(ownerService, name, ct);
            jobId = job.GetProperty("id").GetString()!;
        }

        var podUid = Environment.GetEnvironmentVariable(PodEnv.PodUid) ?? Guid.NewGuid().ToString();
        var trigger = runIdEnv is not null ? "api" : "schedule";

        string runId;
        try
        {
            runId = await client.StartRunAsync(jobId, idempotencyKey: podUid, trigger: trigger, runId: runIdEnv, ct: ct);
        }
        catch (Exception exc)
        {
            buffer.Write(new Dictionary<string, object?>
            {
                ["kind"] = "start_run", ["job_id"] = jobId, ["run_id_env"] = runIdEnv,
                ["name"] = name, ["idempotency_key"] = podUid, ["error"] = exc.Message,
            });
            throw;
        }

        var handlerFlags = ArgsToFlags(handlerArgs);
        var argEnv = ArgsToEnv(handlerArgs);

        var result = await runner(cmdArgv.ToArray(), handlerFlags, argEnv,
            line => client.LogAsync(jobId, runId, line, ct), ct);

        var exitCode = result.ExitCode;
        var summary = result.Summary;
        var status = exitCode == 0 ? "succeeded" : "failed";
        try
        {
            await client.FinishRunAsync(jobId, runId, status, exitCode, summary, ct);
        }
        catch (Exception exc)
        {
            buffer.Write(new Dictionary<string, object?>
            {
                ["kind"] = "finish_run", ["job_id"] = jobId, ["run_id"] = runId,
                ["status"] = status, ["exit_code"] = exitCode, ["error"] = exc.Message,
            });
        }
        return exitCode;
    }
}
