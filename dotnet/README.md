# BrutalSystems.Jobs (.NET SDK)

.NET client SDK for the jobs-service. Three packages:

- `BrutalSystems.Jobs.Core` — wire contract + m2m token minting
- `BrutalSystems.Jobs.Client` — producer HTTP client
- `BrutalSystems.Jobs.Worker` — warm consumer, pod wrapper, observability

Target framework `net10.0`. Versioned independently of the Python SDK as `dotnet-vX.Y.Z`.
