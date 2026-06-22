# jobs-sdk

Official client SDKs for **jobservice** — a job registration, lifecycle, and
dispatch service. The service itself is private; these are the public clients
any consumer uses to register jobs, trigger runs, and handle work.

## Layout

```
python/        # the Python SDK (published to PyPI)
  packages/
    jobs-core/     # the wire contract: Policy, message envelope, JOBS_* pod-env names
    jobs-client/   # the SDK: HTTP client, pod wrapper, warm consumer, handler registry
  tests/contract/  # conformance tests pinning the wire format
dotnet/        # the .NET SDK (planned — published to NuGet)
```

The **wire contract** (`jobs-core`) is the source of truth both SDKs implement.
The contract tests are language-neutral guardrails: any change to the envelope
JSON, queue names, the `JOBS_*` pod-env names, or the `Policy` schema must keep
them green, so the Python and (future) .NET clients can never silently diverge.

## Python — quickstart

```python
from jobs_client import JobsClient, Policy, register_handler

client = JobsClient.from_env(owner_service="my-api")  # base URL from JOBS_SERVICE_URL
await client.register_job(name="lead-gen.score", schedule=None,
                          policy=Policy(execution_mode="in_process", dispatch_mode="warm-queued"))
await client.trigger(job_name="lead-gen.score", args={"lead_id": "01J"})
```

### Develop

[uv](https://docs.astral.sh/uv/) workspace, Python 3.13+.

```sh
cd python
uv sync
uv run pytest
uv run ruff check .
```

## Versioning

The Python and .NET SDKs version independently (tags `python-vX.Y.Z`,
`dotnet-vX.Y.Z`) against the same wire contract. A breaking contract change is a
coordinated major bump across both.
