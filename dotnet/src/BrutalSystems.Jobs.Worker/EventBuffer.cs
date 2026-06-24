using System.Text.Json;
using BrutalSystems.Jobs.Core;

namespace BrutalSystems.Jobs.Worker;

/// <summary>Append-only JSONL buffer for lifecycle calls that fail when the service is
/// unreachable. Port of jobs_client._buffer.EventBuffer (v1: write only).</summary>
public sealed class EventBuffer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path;

    public EventBuffer(string? path = null)
        => _path = path
            ?? Environment.GetEnvironmentVariable(PodEnv.BufferPath)
            ?? PodEnv.BufferPathDefault;

    public void Write(IReadOnlyDictionary<string, object?> @event)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.AppendAllText(_path, JsonSerializer.Serialize(@event, Json) + "\n");
    }
}
