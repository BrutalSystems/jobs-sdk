"""Pod entrypoint that wraps a handler with Jobs service lifecycle calls.

Invoke as: python -m jobs_client.wrapper

Env vars (set by the Jobs service trigger() endpoint or by a CronJob):
    JOBS_OWNER_SERVICE  — Job's owner_service (always required)
    JOBS_TENANT_ID      — Job's tenant (always required)
    JOBS_HANDLER_CMD    — JSON-encoded argv for the handler (always required)
    JOBS_JOB_ID         — registered Job's id (set by triggered dispatch only)
    JOBS_RUN_ID         — pre-allocated Run id (set by triggered dispatch only)
    JOBS_NAME           — Job's name (set by scheduled dispatch only)
    JOBS_HANDLER_ARGS   — JSON dict; flattened to JOBS_ARG_* env + --flag CLI pairs (optional)
"""

from __future__ import annotations

import asyncio
import json
import os
import subprocess
import sys
import uuid
from dataclasses import dataclass
from typing import Any

from jobs_client._buffer import EventBuffer
from jobs_client.client import JobsClient
from jobs_core import pod_env


@dataclass(frozen=True)
class WrapperEnv:
    owner_service: str
    tenant_id: str
    handler_cmd: str
    job_id: str | None
    run_id: str | None
    name: str | None
    handler_args: dict[str, Any]


def _read_env() -> WrapperEnv:
    def _required(name: str) -> str:
        v = os.environ.get(name)
        if not v:
            raise RuntimeError(f"{name} env var required by wrapper")
        return v

    raw_args = os.environ.get(pod_env.HANDLER_ARGS, "{}")
    try:
        handler_args = json.loads(raw_args)
    except json.JSONDecodeError as exc:
        raise RuntimeError(f"JOBS_HANDLER_ARGS is not valid JSON: {exc}") from exc

    return WrapperEnv(
        owner_service=_required(pod_env.OWNER_SERVICE),
        tenant_id=_required(pod_env.TENANT_ID),
        handler_cmd=_required(pod_env.HANDLER_CMD),
        job_id=os.environ.get(pod_env.JOB_ID) or None,
        run_id=os.environ.get(pod_env.RUN_ID) or None,
        name=os.environ.get(pod_env.NAME) or None,
        handler_args=handler_args,
    )


def _args_to_env(args: dict[str, Any]) -> dict[str, str]:
    """Convert handler args (minus the 'handler' key) into JOBS_ARG_* env vars."""
    return {
        pod_env.arg_env_name(k): str(v)
        for k, v in args.items()
        if k != "handler"
    }


def _args_to_flags(args: dict[str, Any]) -> list[str]:
    """Convert handler args into typer/click-style `--flag value` pairs.

    The wrapper exposes args to handlers two ways simultaneously so callers
    can pick the convention that fits: env vars (JOBS_ARG_*) for shell-style
    handlers, CLI flags for typer/click handlers. Booleans become bare
    `--flag` (truthy) or are skipped (falsy). Empty strings still emit the
    flag with an empty value so handlers can override a non-empty default."""
    flags: list[str] = []
    for k, v in args.items():
        if k == "handler":
            continue
        flag = "--" + k.replace("_", "-")
        if isinstance(v, bool):
            if v:
                flags.append(flag)
            continue
        flags.extend([flag, str(v)])
    return flags


_SUMMARY_MARKER = "__JOBS_SUMMARY__"


async def _run(*, client: JobsClient) -> int:
    """Async entrypoint. Returns the handler's exit code.

    Two modes:
    - **Triggered** (sub-project 2): JOBS_JOB_ID and JOBS_RUN_ID set by trigger.py.
      Wrapper uses them directly; start_run transitions the pre-allocated Run.
    - **Scheduled** (sub-project 3, cron): JOBS_NAME set in the CronJob manifest.
      Wrapper resolves Job.id via get_job_by_name; start_run allocates a fresh Run.
    """
    env = _read_env()
    buffer = EventBuffer()

    try:
        cmd_argv = json.loads(env.handler_cmd)
    except json.JSONDecodeError as exc:
        raise RuntimeError(f"JOBS_HANDLER_CMD must be a JSON list: {exc}") from exc
    if not isinstance(cmd_argv, list) or not all(isinstance(c, str) for c in cmd_argv):
        raise RuntimeError(
            f"JOBS_HANDLER_CMD must be a JSON list of strings, got: {env.handler_cmd!r}"
        )
    if not cmd_argv:
        raise RuntimeError(
            f"JOBS_HANDLER_CMD must be a non-empty JSON list, got: {env.handler_cmd!r}"
        )
    cmd = cmd_argv + _args_to_flags(env.handler_args)

    if env.job_id:
        job_id = env.job_id
    else:
        if not env.name:
            raise RuntimeError("either JOBS_JOB_ID or JOBS_NAME env var required by wrapper")
        try:
            job = await client.get_job_by_name(
                owner_service=env.owner_service, name=env.name,
            )
        except Exception as exc:
            raise RuntimeError(
                f"failed to resolve Job by name {env.name!r}: {exc}"
            ) from exc
        job_id = job["id"]

    pod_uid = os.environ.get(pod_env.POD_UID) or str(uuid.uuid4())
    trigger = "api" if env.run_id else "schedule"

    try:
        run_id = await client.start_run(
            job_id=job_id, run_id=env.run_id,
            idempotency_key=pod_uid, trigger=trigger,
        )
    except Exception as exc:
        buffer.write({
            "kind": "start_run", "job_id": job_id,
            "run_id_env": env.run_id, "name": env.name,
            "idempotency_key": pod_uid, "error": str(exc),
        })
        raise

    sub_env = dict(os.environ)
    sub_env.update(_args_to_env(env.handler_args))

    proc = subprocess.Popen(
        cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        text=True, env=sub_env,
    )
    summary: dict[str, Any] | None = None
    for line in proc.stdout:
        sys.stdout.write(line)
        try:
            await client.log(job_id=job_id, run_id=run_id, line=line.rstrip("\n"))
        except Exception:
            pass  # log is best-effort
        if line.startswith(_SUMMARY_MARKER):
            try:
                summary = json.loads(line[len(_SUMMARY_MARKER):].strip())
            except json.JSONDecodeError:
                summary = None

    exit_code = proc.wait()
    status = "succeeded" if exit_code == 0 else "failed"

    try:
        await client.finish_run(
            job_id=job_id, run_id=run_id,
            status=status, exit_code=exit_code, summary=summary,
        )
    except Exception as exc:
        buffer.write({
            "kind": "finish_run", "job_id": job_id, "run_id": run_id,
            "status": status, "exit_code": exit_code, "summary": summary,
            "error": str(exc),
        })

    return exit_code


def main() -> None:
    # The producer injects JOBS_OWNER_SERVICE / JOBS_TENANT_ID into the pod;
    # from_env resolves them (and raises if owner is unset). No app-specific
    # default — the owning service names itself.
    client = JobsClient.from_env()
    exit_code = asyncio.run(_run(client=client))
    sys.exit(exit_code)


if __name__ == "__main__":
    main()
