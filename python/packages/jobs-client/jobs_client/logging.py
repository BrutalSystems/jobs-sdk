"""Contextvars-based per-run log binding.

Warm consumer processes are long-lived and handle many runs; we can't set
env vars per-run the way the wrapper does. Instead, bind a contextvars dict
around each handler call and use a logging Filter to push those keys onto
every LogRecord produced inside the block.

Downstream JSON log formatters can read these attributes via `record.run_id`
etc. Plain text formatters can use `%(run_id)s` in their format strings; the
filter sets a sentinel `""` for any field the formatter requests but that
wasn't bound, to avoid KeyError on plain-text formats."""

from __future__ import annotations

import contextlib
import contextvars
import logging
from collections.abc import Iterator
from typing import Any

_context: contextvars.ContextVar[dict[str, Any] | None] = contextvars.ContextVar(
    "jobs_log_context", default=None,
)


@contextlib.contextmanager
def bind_log_context(**kwargs: Any) -> Iterator[None]:
    """Bind keyword arguments to the current async/thread context. Inner
    contexts override outer ones; restoration on exit is automatic."""
    current = _context.get() or {}
    merged = {**current, **kwargs}
    token = _context.set(merged)
    try:
        yield
    finally:
        _context.reset(token)


class _ContextInjectingFilter(logging.Filter):
    def filter(self, record: logging.LogRecord) -> bool:
        for key, value in (_context.get() or {}).items():
            # Don't clobber explicit `extra={...}` kwargs
            if not hasattr(record, key):
                setattr(record, key, value)
        return True


def install_context_filter() -> logging.Filter:
    """Returns a logging Filter that injects bound context onto records.
    Attach it to whichever loggers you want context-aware (typically the
    root logger in the warm consumer)."""
    return _ContextInjectingFilter()
