"""Optional OpenTelemetry trace-context propagation for the pod wrapper.

The SDK does NOT depend on OpenTelemetry — handlers must run fine without it.
When the OTEL API is installed and a tracer provider is configured (e.g. the pod
runs under `opentelemetry-instrument`), the wrapper extracts the dispatch trace
context the service injected as JOBS_TRACEPARENT, runs the run-lifecycle under a
child `jobs.run` span, and forwards that span's context to the handler subprocess
so the handler's spans link too (server -> wrapper -> handler).

Every degraded path still forwards the *incoming* context unchanged, so a
downstream instrumented handler can parent under the server's dispatch span even
when the wrapper itself can't emit spans:
  - OTEL not installed                -> forward incoming, no span
  - OTEL API only (no provider/SDK)   -> forward incoming, no-op span
  - OTEL fully configured             -> forward the wrapper span's context
"""

from __future__ import annotations

import os
from collections.abc import Iterator
from contextlib import contextmanager

from jobs_core import pod_env

# pod-env name <-> W3C carrier key. The carrier keys are what the propagator
# reads/writes; the env names are the cross-pod contract in jobs_core.pod_env.
_CARRIER_KEYS: tuple[tuple[str, str], ...] = (
    (pod_env.TRACEPARENT, "traceparent"),
    (pod_env.TRACESTATE, "tracestate"),
)


def _incoming_carrier() -> dict[str, str]:
    """W3C carrier built from the trace-context env the service injected."""
    carrier: dict[str, str] = {}
    for env_name, w3c_key in _CARRIER_KEYS:
        value = os.environ.get(env_name)
        if value:
            carrier[w3c_key] = value
    return carrier


def _carrier_to_pod_env(carrier: dict[str, str]) -> dict[str, str]:
    """Map a W3C carrier back to the JOBS_* env vars handed to the subprocess."""
    return {
        env_name: carrier[w3c_key]
        for env_name, w3c_key in _CARRIER_KEYS
        if carrier.get(w3c_key)
    }


@contextmanager
def handler_trace_context() -> Iterator[dict[str, str]]:
    """Parent the run under the dispatch trace; yield env vars to forward the
    active trace context to the handler subprocess.

    Always yields a dict (possibly empty) safe to merge into the subprocess env.
    See the module docstring for the degraded-path behavior."""
    incoming = _incoming_carrier()
    try:
        from opentelemetry import context as otel_context
        from opentelemetry import trace
        from opentelemetry.propagate import extract, inject
    except ImportError:
        yield _carrier_to_pod_env(incoming)
        return

    parent = extract(incoming) if incoming else None
    token = otel_context.attach(parent) if parent is not None else None
    try:
        tracer = trace.get_tracer("jobs_client.wrapper")
        with tracer.start_as_current_span("jobs.run"):
            outgoing: dict[str, str] = {}
            inject(outgoing)
            # With no configured provider, inject is a no-op (invalid span
            # context) — fall back to the incoming context so linkage survives.
            yield _carrier_to_pod_env(outgoing or incoming)
    finally:
        if token is not None:
            otel_context.detach(token)
