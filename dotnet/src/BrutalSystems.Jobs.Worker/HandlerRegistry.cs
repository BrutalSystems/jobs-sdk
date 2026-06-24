namespace BrutalSystems.Jobs.Worker;

/// <summary>A warm-queued job handler. `args` are the envelope's variadic args; the optional
/// return dict becomes the Run's summary. At-least-once delivery means handlers MUST be re-entrant.</summary>
public delegate Task<IReadOnlyDictionary<string, object?>?> JobHandler(
    IReadOnlyDictionary<string, object?> args, CancellationToken ct);

/// <summary>Registry of warm-queued handlers. Injectable replacement for Python's module-global
/// HANDLERS dict. Duplicate registration fails loud at boot.</summary>
public sealed class HandlerRegistry
{
    private readonly Dictionary<string, JobHandler> _handlers = new();

    public void Register(string jobName, JobHandler handler)
    {
        if (!_handlers.TryAdd(jobName, handler))
            throw new InvalidOperationException($"handler for '{jobName}' already registered");
    }

    public bool TryGet(string jobName, out JobHandler handler) => _handlers.TryGetValue(jobName, out handler!);

    public bool Contains(string jobName) => _handlers.ContainsKey(jobName);
}
