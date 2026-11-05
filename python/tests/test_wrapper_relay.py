"""The wrapper relays handler stdout line-by-line to its own stdout.

It MUST flush after each line. The pod's stdout is block-buffered (not a TTY),
so an unflushed relay means `kubectl logs -f` lags and — worse — a SIGKILL
(spot eviction, OOM, activeDeadlineSeconds) discards the buffered tail, losing
exactly the error output needed to debug the failure. The handler's own logging
flushes per-record into the pipe; the wrapper must not re-buffer it on the way
out.
"""

from __future__ import annotations

import json
import sys

from jobs_client import wrapper
from jobs_core import pod_env


class _FakeClient:
    """Minimal JobsClient stand-in: records nothing we assert on except that the
    lifecycle calls don't raise, so _run reaches the relay loop."""

    async def get_job_by_name(self, **kw):
        return {"id": "job-1"}

    async def start_run(self, **kw):
        return "run-1"

    async def log(self, *, job_id, run_id, line):
        pass

    async def finish_run(self, **kw):
        pass


class _RecordingStdout:
    """Stand-in for sys.stdout that records writes and whether each write was
    followed by a flush before the next write."""

    def __init__(self):
        self.chunks: list[str] = []
        self.flushes_after_write = 0
        self._unflushed = False

    def write(self, s):
        self.chunks.append(s)
        self._unflushed = True

    def flush(self):
        if self._unflushed:
            self.flushes_after_write += 1
            self._unflushed = False


async def test_relay_flushes_stdout_after_each_line(monkeypatch):
    monkeypatch.setenv(pod_env.OWNER_SERVICE, "aws-cost")
    monkeypatch.setenv(pod_env.TENANT_ID, "t-1")
    monkeypatch.setenv(pod_env.JOB_ID, "job-1")
    monkeypatch.setenv(
        pod_env.HANDLER_CMD,
        json.dumps([sys.executable, "-c", "print('first'); print('second')"]),
    )

    rec = _RecordingStdout()
    monkeypatch.setattr(sys, "stdout", rec)

    rc = await wrapper._run(client=_FakeClient())

    assert rc == 0
    relayed = "".join(rec.chunks)
    assert "first" in relayed and "second" in relayed
    # Two handler lines => stdout flushed after each one (no buffered tail left
    # to be lost on SIGKILL).
    assert rec.flushes_after_write >= 2
