using System.Text.Json.Serialization;

namespace BrutalSystems.Jobs.Core;

public sealed record Resources(
    [property: JsonPropertyName("cpu_request")] string CpuRequest,
    [property: JsonPropertyName("cpu_limit")] string CpuLimit,
    [property: JsonPropertyName("mem_request")] string MemRequest,
    [property: JsonPropertyName("mem_limit")] string MemLimit);

/// <summary>Job execution policy. 1:1 with jobs_core.policy.Policy, including
/// strict unknown-field rejection (Python's extra="forbid") so producer/service
/// version skew fails loud instead of silently dropping fields.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Policy
{
    [JsonPropertyName("execution_mode")] public required string ExecutionMode { get; init; }
    [JsonPropertyName("concurrency")] public int Concurrency { get; init; } = 1;
    [JsonPropertyName("resources")] public Resources? Resources { get; init; }
    [JsonPropertyName("image")] public string? Image { get; init; }
    [JsonPropertyName("command")] public IReadOnlyList<string>? Command { get; init; }
    [JsonPropertyName("env_from")] public IReadOnlyList<string> EnvFrom { get; init; } = [];
    [JsonPropertyName("config_map_env_from")] public IReadOnlyList<string> ConfigMapEnvFrom { get; init; } = [];
    [JsonPropertyName("timeout_seconds")] public int TimeoutSeconds { get; init; } = 3600;
    [JsonPropertyName("prefer_spot")] public bool PreferSpot { get; init; }
    [JsonPropertyName("node_arch")] public string NodeArch { get; init; } = "any";
    [JsonPropertyName("service_account")] public string? ServiceAccount { get; init; }
    [JsonPropertyName("orchestrator_overrides")] public IReadOnlyDictionary<string, object?> OrchestratorOverrides { get; init; }
        = new Dictionary<string, object?>();
    [JsonPropertyName("dispatch_mode")] public string DispatchMode { get; init; } = "direct";
}
