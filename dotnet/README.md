# BrutalSystems.Jobs (.NET SDK)

.NET client SDK for the jobs-service. Three packages:

- `BrutalSystems.Jobs.Core` — wire contract + m2m token minting
- `BrutalSystems.Jobs.Client` — producer HTTP client
- `BrutalSystems.Jobs.Worker` — warm consumer, pod wrapper, observability

Target framework `net10.0`. Versioned independently of the Python SDK as `dotnet-vX.Y.Z`.

## Producer

```csharp
using BrutalSystems.Jobs.Client;
using BrutalSystems.Jobs.Core;

var client = JobsClient.FromEnv(httpClient, ownerService: "my-api");   // base URL from JOBS_SERVICE_URL
await client.RegisterJobAsync("lead-gen.score", schedule: null,
    new Policy { ExecutionMode = "in_process", DispatchMode = "warm-queued" });
await client.TriggerAsync("lead-gen.score", args: new Dictionary<string, object?> { ["lead_id"] = "01J" });
```

## Install (local dev-bridge feed)

Wire up the `local-nuget/` feed by adding a `nuget.config` alongside your `.csproj`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="local-nuget" value="./local-nuget" />
  </packageSources>
</configuration>
```

Then reference the packages:

```xml
<PackageReference Include="BrutalSystems.Jobs.Core"   Version="0.1.0-dev" />
<PackageReference Include="BrutalSystems.Jobs.Client" Version="0.1.0-dev" />
<PackageReference Include="BrutalSystems.Jobs.Worker" Version="0.1.0-dev" />
```

## Warm consumer

```csharp
using BrutalSystems.Jobs.Worker;

var registry = new HandlerRegistry();
registry.Register("lead-gen.score", async (args, ct) =>
{
    // ... do work; return a summary or null
    return new Dictionary<string, object?> { ["scored"] = true };
});

var consumer = new WarmConsumer(WarmConsumerConfig.FromEnv(), client, registry, new JobsMetrics(), logger);
await consumer.RunAsync(ct);
```
