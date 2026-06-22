"""Contract: the AMQP message envelope + queue topology.

The envelope JSON is what a producer publishes and a consumer parses; the queue
names determine where messages land. Both are shared by the service and the SDK
across the broker. Pinning the exact JSON keys and queue-name strings here means
a field rename or naming-logic change can't silently desync the two halves."""

from __future__ import annotations

from datetime import UTC, datetime

from jobs_core import (
    EXCHANGE_NAME,
    MessageEnvelope,
    dead_queue_name,
    retry_queue_name,
    work_queue_name,
)


def test_exchange_name_is_frozen():
    assert EXCHANGE_NAME == "jobs"


def test_envelope_json_keys_are_frozen():
    env = MessageEnvelope(
        run_id="01R", job_id="01J", tenant_id="_org", owner_service="example-api",
        job_name="lead-gen.score", dispatched_at=datetime(2026, 6, 21, tzinfo=UTC),
    )
    import json
    payload = json.loads(env.to_json_bytes())
    assert set(payload) == {
        "run_id", "job_id", "tenant_id", "owner_service",
        "job_name", "args", "attempt", "dispatched_at",
    }
    assert payload["args"] == {}     # default
    assert payload["attempt"] == 1   # default


def test_envelope_round_trips():
    env = MessageEnvelope(
        run_id="01R", job_id="01J", tenant_id="t", owner_service="o",
        job_name="n", args={"k": "v"}, attempt=3,
        dispatched_at=datetime(2026, 6, 21, tzinfo=UTC),
    )
    back = MessageEnvelope.from_json_bytes(env.to_json_bytes())
    assert back.model_dump() == env.model_dump()


def test_queue_naming_is_frozen():
    # warm-queued: base name, no suffix
    warm = work_queue_name(owner_service="example-api", job_name="x", dispatch_mode="warm-queued")
    assert warm == "jobs.example-api.x"
    # cold-queued: .cold suffix so one dispatcher can consume all cold queues
    cold = work_queue_name(owner_service="example-api", job_name="x", dispatch_mode="cold-queued")
    assert cold == "jobs.example-api.x.cold"
    # retry/dead strip the .cold base so warm + cold share retry naming
    assert retry_queue_name(warm) == "jobs.example-api.x.retry"
    assert dead_queue_name(warm) == "jobs.example-api.x.dead"
    assert retry_queue_name(cold) == "jobs.example-api.x.retry"
    assert dead_queue_name(cold) == "jobs.example-api.x.dead"
