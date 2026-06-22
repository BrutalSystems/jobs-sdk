"""AMQP envelope + queue-name helpers for the `jobs` exchange.

The envelope is the only place the wire JSON shape is defined. The publisher
(jobs-service trigger route), the cold dispatcher, and the warm consumer (in
the SDK) all round-trip through it. Before extraction the SDK's warm_consumer
carried its own hardcoded `_EXCHANGE_NAME = "jobs"` copy; both sides now import
these names so the exchange/queue topology can never disagree."""

from __future__ import annotations

from datetime import datetime
from typing import Any

from pydantic import BaseModel, Field

EXCHANGE_NAME = "jobs"


def work_queue_name(*, owner_service: str, job_name: str, dispatch_mode: str) -> str:
    """Compose the per-Job work queue name. Warm and cold get distinct queues
    so a single dispatcher pod can consume all cold queues regardless of owner."""
    base = f"jobs.{owner_service}.{job_name}"
    if dispatch_mode == "cold-queued":
        return f"{base}.cold"
    return base  # warm-queued


def retry_queue_name(work_queue: str) -> str:
    """Stripping any `.cold` suffix lets warm + cold share retry naming logic."""
    base = work_queue[: -len(".cold")] if work_queue.endswith(".cold") else work_queue
    return f"{base}.retry"


def dead_queue_name(work_queue: str) -> str:
    base = work_queue[: -len(".cold")] if work_queue.endswith(".cold") else work_queue
    return f"{base}.dead"


class MessageEnvelope(BaseModel):
    """Serialized as JSON in AMQP message body. Persistent delivery.

    `attempt` is mirrored into AMQP headers by the publisher/consumer so
    redelivery preserves it across consumer crashes."""

    run_id: str
    job_id: str
    tenant_id: str
    owner_service: str
    job_name: str
    args: dict[str, Any] = Field(default_factory=dict)
    attempt: int = 1
    dispatched_at: datetime

    def to_json_bytes(self) -> bytes:
        return self.model_dump_json().encode("utf-8")

    @classmethod
    def from_json_bytes(cls, body: bytes) -> MessageEnvelope:
        try:
            return cls.model_validate_json(body)
        except Exception as exc:
            raise ValueError(f"malformed envelope JSON: {exc}") from exc
