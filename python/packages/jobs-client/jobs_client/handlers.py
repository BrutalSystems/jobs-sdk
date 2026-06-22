"""Registry of Python callable handlers for warm-queued Jobs.

Owner-services import this module (at process startup, before the warm
consumer reads the registry) and call `register_handler(job_name, fn)` to
declare handlers. The warm consumer looks up handlers here at message
processing time.

**At-least-once contract**: messages may be redelivered after consumer
crashes. Handlers MUST be re-entrant. The Run record's idempotency_key
guarantees server-side de-duplication of lifecycle calls, but if your
handler has side effects on external systems, you own that idempotency."""

from __future__ import annotations

from collections.abc import Callable
from typing import Any

HANDLERS: dict[str, Callable[..., dict[str, Any] | None]] = {}


def register_handler(
    job_name: str,
    fn: Callable[..., dict[str, Any] | None],
) -> None:
    """Register a callable handler for a warm-queued Job.

    Raises ValueError if the job_name is already registered (we'd rather
    fail loud at boot than have two handlers race for the same message).
    Raises TypeError if fn isn't callable."""
    if not callable(fn):
        raise TypeError(f"handler for {job_name!r} must be callable, got {type(fn).__name__}")
    if job_name in HANDLERS:
        raise ValueError(f"handler for {job_name!r} already registered")
    HANDLERS[job_name] = fn
