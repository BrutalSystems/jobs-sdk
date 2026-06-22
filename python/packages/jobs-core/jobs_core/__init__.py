"""jobs-core — the shared wire contract for jobservice.

One definition of everything that crosses the boundary between the SDK
(jobs-client) and the service (jobs-service): the Job execution Policy, the AMQP
message envelope + queue topology, and the JOBS_* pod-env contract. Both
packages depend on this one so the contract can never drift between them."""

from __future__ import annotations

from jobs_core import pod_env
from jobs_core.envelope import (
    EXCHANGE_NAME,
    MessageEnvelope,
    dead_queue_name,
    retry_queue_name,
    work_queue_name,
)
from jobs_core.policy import Policy, Resources

__all__ = [
    "EXCHANGE_NAME",
    "MessageEnvelope",
    "Policy",
    "Resources",
    "dead_queue_name",
    "pod_env",
    "retry_queue_name",
    "work_queue_name",
]
