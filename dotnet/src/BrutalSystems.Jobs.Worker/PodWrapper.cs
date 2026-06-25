using System.Diagnostics;
using System.Text.Json;
using BrutalSystems.Jobs.Client;
using BrutalSystems.Jobs.Core;

namespace BrutalSystems.Jobs.Worker;

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

    /// <summary>Full pod lifecycle. Reads JOBS_* env, resolves the job id, starts the run, spawns the
    /// handler subprocess, streams stdout to logs + summary marker, finishes the run, buffering on
    /// service errors. Stderr is drained concurrently to avoid the classic stdout/stderr pipe deadlock.</summary>
    public static async Task<int> RunAsync(JobsClient client, CancellationToken ct = default)
    {
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
        foreach (var f in ArgsToFlags(handlerArgs)) psi.ArgumentList.Add(f);
        foreach (var (k, v) in ArgsToEnv(handlerArgs)) psi.Environment[k] = v;

        using var proc = Process.Start(psi)!;
        IReadOnlyDictionary<string, object?>? summary = null;

        // Drain stderr concurrently on a background task so it never blocks the stdout loop
        // or WaitForExitAsync when the stderr pipe buffer fills.
        var stderrDrain = Task.Run(async () =>
        {
            string? errLine;
            while ((errLine = await proc.StandardError.ReadLineAsync()) is not null)
                Console.Error.WriteLine(errLine);
        });

        string? line;
        while ((line = await proc.StandardOutput.ReadLineAsync(ct)) is not null)
        {
            Console.WriteLine(line);
            await client.LogAsync(jobId, runId, line, ct);
            summary = ExtractSummary(line) ?? summary;
        }

        await stderrDrain;
        await proc.WaitForExitAsync(ct);

        var exitCode = proc.ExitCode;
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
