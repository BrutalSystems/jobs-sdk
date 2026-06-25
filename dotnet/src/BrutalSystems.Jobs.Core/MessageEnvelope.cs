using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrutalSystems.Jobs.Core;

/// <summary>AMQP message body for the `jobs` exchange. 1:1 with jobs_core.envelope.
/// MessageEnvelope. `attempt` is mirrored into AMQP headers by the publisher/consumer
/// so redelivery preserves it across crashes.</summary>
public sealed record MessageEnvelope
{
    [JsonPropertyName("run_id")] public required string RunId { get; init; }
    [JsonPropertyName("job_id")] public required string JobId { get; init; }
    [JsonPropertyName("tenant_id")] public required string TenantId { get; init; }
    [JsonPropertyName("owner_service")] public required string OwnerService { get; init; }
    [JsonPropertyName("job_name")] public required string JobName { get; init; }
    [JsonPropertyName("args")] public IReadOnlyDictionary<string, object?> Args { get; init; }
        = new Dictionary<string, object?>();
    [JsonPropertyName("attempt")] public int Attempt { get; init; } = 1;
    [JsonPropertyName("dispatched_at")] public required DateTimeOffset DispatchedAt { get; init; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public byte[] ToJsonBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    public static MessageEnvelope FromJsonBytes(ReadOnlySpan<byte> body)
    {
        try
        {
            var env = JsonSerializer.Deserialize<MessageEnvelope>(body, Json)
                ?? throw new FormatException("malformed envelope JSON: null");
            // STJ deserializes object? values as JsonElement. Normalize Args to native CLR values
            // (string/long/double/bool/null/nested) so handlers receive the same types whether the
            // envelope came off the wire or was constructed in-process.
            return env with { Args = NativeArgs(env.Args) };
        }
        catch (JsonException exc)
        {
            throw new FormatException($"malformed envelope JSON: {exc.Message}", exc);
        }
    }

    private static IReadOnlyDictionary<string, object?> NativeArgs(IReadOnlyDictionary<string, object?> args)
    {
        var result = new Dictionary<string, object?>(args.Count);
        foreach (var (k, v) in args) result[k] = ToNative(v);
        return result;
    }

    private static object? ToNative(object? value) => value switch
    {
        JsonElement e => e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.TryGetInt64(out var l) ? (object?)l : e.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => ToNative(p.Value)),
            JsonValueKind.Array => e.EnumerateArray().Select(elem => ToNative(elem)).ToList(),
            _ => e.ToString(),
        },
        _ => value,
    };
}
