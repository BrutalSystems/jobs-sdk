"""The wrapper's trace-context propagation (jobs_client._tracing).

server -> wrapper -> handler stays one trace: the service injects JOBS_TRACEPARENT
into the pod env, the wrapper runs the lifecycle under a child span, and forwards
the active context to the handler subprocess env. With OTEL installed these tests
cover the active path; the incoming-only fallbacks are covered structurally."""

from __future__ import annotations

import pytest

from jobs_client._tracing import handler_trace_context
from jobs_core import pod_env

# A valid, sampled W3C traceparent (flags=01); trace id is the 32-hex middle.
_PARENT_TRACE_ID = "0af7651916cd43dd8448eb211c80319c"
_INCOMING = f"00-{_PARENT_TRACE_ID}-b7ad6b7169203331-01"


@pytest.fixture(scope="session", autouse=True)
def _configured_tracer_provider():
    """Install a real SDK TracerProvider once so spans are sampled and inject
    emits a traceparent (the default API provider is a no-op that emits nothing).
    OTEL forbids overriding a set provider, so this is session-scoped and not
    restored — every test in this module runs with tracing active."""
    from opentelemetry import trace
    from opentelemetry.sdk.trace import TracerProvider

    trace.set_tracer_provider(TracerProvider())
    yield


def test_forwards_incoming_context_to_subprocess_env(monkeypatch):
    monkeypatch.setenv(pod_env.TRACEPARENT, _INCOMING)
    with handler_trace_context() as sub_env:
        tp = sub_env[pod_env.TRACEPARENT]
        # A fresh span id under the same trace => same trace id, new span.
        assert tp.startswith(f"00-{_PARENT_TRACE_ID}-")
        assert tp != _INCOMING


def test_starts_root_span_when_no_incoming_context(monkeypatch):
    """No JOBS_TRACEPARENT but a configured provider: the wrapper still starts a
    span, so the handler is traced under a fresh root rather than orphaned. The
    forwarded context is a well-formed traceparent on a *different* trace."""
    monkeypatch.delenv(pod_env.TRACEPARENT, raising=False)
    monkeypatch.delenv(pod_env.TRACESTATE, raising=False)
    with handler_trace_context() as sub_env:
        tp = sub_env[pod_env.TRACEPARENT]
        assert tp.startswith("00-")
        assert _PARENT_TRACE_ID not in tp  # not the unrelated incoming trace


def test_tracestate_is_carried_when_present(monkeypatch):
    monkeypatch.setenv(pod_env.TRACEPARENT, _INCOMING)
    monkeypatch.setenv(pod_env.TRACESTATE, "vendor=value")
    with handler_trace_context() as sub_env:
        assert sub_env.get(pod_env.TRACESTATE) == "vendor=value"


def test_always_yields_a_mergeable_dict(monkeypatch):
    """Contract: the yielded value is always a dict safe to merge into env, even
    with no trace context at all."""
    monkeypatch.delenv(pod_env.TRACEPARENT, raising=False)
    with handler_trace_context() as sub_env:
        assert isinstance(sub_env, dict)
        env = {"X": "1", **sub_env}  # merge must not raise
        assert env["X"] == "1"


def test_degrades_to_forwarding_when_otel_absent(monkeypatch):
    """Without OpenTelemetry installed the wrapper can't emit a span, but it must
    still forward the incoming context verbatim so a downstream instrumented
    handler links to the server's dispatch span."""
    import sys

    monkeypatch.setenv(pod_env.TRACEPARENT, _INCOMING)
    monkeypatch.setenv(pod_env.TRACESTATE, "vendor=value")
    # Make `from opentelemetry import ...` raise ImportError.
    monkeypatch.setitem(sys.modules, "opentelemetry", None)
    with handler_trace_context() as sub_env:
        assert sub_env[pod_env.TRACEPARENT] == _INCOMING  # unchanged passthrough
        assert sub_env[pod_env.TRACESTATE] == "vendor=value"
