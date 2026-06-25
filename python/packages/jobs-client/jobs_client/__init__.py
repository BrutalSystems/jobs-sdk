"""Python SDK for the Jobs service.

Typical usage:

    from jobs_client import JobsClient, Policy, Resources

    jobs = JobsClient.from_env(owner_service="my-api")
    await jobs.register_job(name="...", schedule=None, policy=Policy(...))
    result = await jobs.trigger(job_name="...", args={"handler": "..."})

For warm-queued Jobs, register handlers and let the warm consumer dispatch:

    from jobs_client import register_handler, TransientError

    def score_one_lead(*, lead_id: str) -> dict:
        ...

    register_handler("lead-gen.score", score_one_lead)
"""

# Policy/Resources are the shared wire contract; re-exported from jobs_core so
# existing `from jobs_client import Policy` call sites keep working.
from jobs_core import Policy, Resources

from ._tracing import adopt_trace_context
from .client import JobsClient
from .exceptions import TransientError
from .handlers import HANDLERS, register_handler

__all__ = [
    "JobsClient",
    "Policy",
    "Resources",
    "TransientError",
    "HANDLERS",
    "register_handler",
    "adopt_trace_context",
]
