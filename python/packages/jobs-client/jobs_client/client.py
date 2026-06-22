"""HTTP client for the Jobs service.

All methods are async (httpx.AsyncClient under the hood). Construct via
JobsClient.from_env() in production code; tests can construct directly."""

from __future__ import annotations

import asyncio
import os
from collections.abc import Callable
from typing import Any

import httpx

from jobs_client.auth import TokenProvider
from jobs_core import Policy, pod_env


class JobNotFoundError(Exception):
    """Jobs service returned 404 for the named Job."""


class RunNotFoundError(Exception):
    """Jobs service returned 404 for the named Run lookup."""


class JobsClient:
    def __init__(
        self,
        *,
        base_url: str,
        token_provider: Callable[[], str],
        timeout_seconds: float = 10.0,
    ):
        self._base_url = base_url.rstrip("/")
        self._token_provider = token_provider
        self._timeout = timeout_seconds

    @classmethod
    def from_env(
        cls, *, owner_service: str | None = None, tenant_id: str | None = None,
    ) -> JobsClient:
        """Construct from the environment. Resolution is explicit arg → env var →
        fallback. `owner_service` has no app-specific fallback (any constant would
        re-bake a deployment identity into the shared SDK), so it raises if neither
        the arg nor JOBS_OWNER_SERVICE is set. `tenant_id` falls back to "_org"."""
        base_url = os.environ.get(pod_env.SERVICE_URL)
        if not base_url:
            raise RuntimeError(f"{pod_env.SERVICE_URL} env var required")
        owner_service = owner_service or os.environ.get(pod_env.OWNER_SERVICE)
        if not owner_service:
            raise RuntimeError(
                f"owner_service required: pass it or set {pod_env.OWNER_SERVICE}"
            )
        tenant_id = tenant_id or os.environ.get(pod_env.TENANT_ID) or "_org"
        provider = TokenProvider(owner_service=owner_service, tenant_id=tenant_id)
        return cls(base_url=base_url, token_provider=provider)

    def _headers(self) -> dict[str, str]:
        return {"Authorization": f"Bearer {self._token_provider()}"}

    async def register_job(
        self,
        *,
        name: str,
        schedule: str | None,
        policy: Policy,
    ) -> str:
        """Idempotent upsert. Returns the job_id."""
        body = {
            "name": name,
            "schedule": schedule,
            "policy": policy.model_dump(),
        }
        async with httpx.AsyncClient(timeout=self._timeout) as http:
            r = await http.post(
                f"{self._base_url}/api/v1/jobs",
                json=body,
                headers=self._headers(),
            )
            r.raise_for_status()
            return r.json()["job_id"]

    async def trigger(
        self,
        *,
        job_name: str,
        cmd: list[str] | None = None,
        args: dict[str, Any] | None = None,
        run_tenant: str | None = None,
        run_user: str | None = None,
        on_miss_register: Callable[[], Any] | None = None,
    ) -> dict[str, str]:
        """Trigger a Run on a registered Job.

        Parameters
        ----------
        cmd : list[str] | None
            The handler argv. Forwarded into the spawned pod as JOBS_HANDLER_CMD.
            Required for direct-dispatch (this method); scheduled Jobs read it
            from their CronJob manifest's env block instead.
        args : dict | None
            Per-trigger handler args. Forwarded as JOBS_HANDLER_ARGS; the wrapper
            maps it to `--flag value` pairs + JOBS_ARG_* env vars.
        run_tenant : str | None
            When triggering on behalf of a user, the user's tenant. Becomes the
            run's tenant (realtime scope + discovery) instead of the job's owner
            tenant. Omit for background/system runs.
        run_user : str | None
            The id of the user the run is triggered for (optional).

        If the Jobs service returns 404 and `on_miss_register` is provided,
        invoke it and retry once. Returns {"run_id", "external_ref", "job_id"}."""
        body: dict[str, Any] = {"args": args or {}}
        if cmd is not None:
            body["cmd"] = cmd
        if run_tenant is not None:
            body["run_tenant"] = run_tenant
        if run_user is not None:
            body["run_user"] = run_user
        url = f"{self._base_url}/api/v1/jobs/by-name/{job_name}/trigger"

        async with httpx.AsyncClient(timeout=self._timeout) as http:
            r = await http.post(url, json=body, headers=self._headers())
            if r.status_code == 404 and on_miss_register is not None:
                await on_miss_register()
                r = await http.post(url, json=body, headers=self._headers())
            if r.status_code == 404:
                raise JobNotFoundError(f"job {job_name!r} not registered")
            r.raise_for_status()
            return r.json()

    async def get_job_by_name(
        self, *, owner_service: str, name: str,
    ) -> dict[str, Any]:
        """Look up a registered Job by (owner_service, name) within the caller's tenant.

        Used by the wrapper at boot to resolve Job.id from env-supplied (owner, name).
        Tenant scope is implied by the bearer token's `tenant_id` claim."""
        async with httpx.AsyncClient(timeout=self._timeout) as http:
            r = await http.get(
                f"{self._base_url}/api/v1/jobs/by-name/{name}",
                headers=self._headers(),
            )
            if r.status_code == 404:
                raise JobNotFoundError(f"job {name!r} not registered for owner {owner_service!r}")
            r.raise_for_status()
            return r.json()

    async def list_jobs_filtered(
        self,
        *,
        owner_service: str | None = None,
        dispatch_mode: str | None = None,
    ) -> list[dict[str, Any]]:
        """List Jobs in the caller's tenant, optionally filtered.

        Note: the server's GET /api/v1/jobs route returns ``list[JobOut]``
        directly (verified in Task 9), not a ``{"jobs": [...]}`` wrapper, so
        we return ``r.json()`` unmodified."""
        params: dict[str, str] = {}
        if owner_service is not None:
            params["owner_service"] = owner_service
        if dispatch_mode is not None:
            params["dispatch_mode"] = dispatch_mode
        async with httpx.AsyncClient(timeout=self._timeout) as http:
            r = await http.get(
                f"{self._base_url}/api/v1/jobs",
                params=params, headers=self._headers(),
            )
            r.raise_for_status()
            return r.json()

    async def get_run_by_external_ref(
        self, *, external_ref: str,
    ) -> dict[str, Any]:
        """Look up a Run by its external_ref (k8s Job name) within the caller's tenant."""
        async with httpx.AsyncClient(timeout=self._timeout) as http:
            r = await http.get(
                f"{self._base_url}/api/v1/runs/by-external-ref/{external_ref}",
                headers=self._headers(),
            )
            if r.status_code == 404:
                raise RunNotFoundError(f"no run with external_ref={external_ref!r}")
            r.raise_for_status()
            return r.json()

    async def list_jobs(self, *, tenant_id: str) -> list[dict[str, Any]]:
        """List Jobs for the caller's tenant. Tenant scoping comes from the
        bearer token's claim; `tenant_id` here is self-documenting only."""
        async with httpx.AsyncClient(timeout=self._timeout) as http:
            r = await http.get(
                f"{self._base_url}/api/v1/jobs",
                headers=self._headers(),
            )
            r.raise_for_status()
            return r.json()

    async def list_runs(
        self, *,
        job_id: str,
        tenant_id: str,
        limit: int = 50,
    ) -> list[dict[str, Any]]:
        """List Runs for a Job within the caller's tenant."""
        async with httpx.AsyncClient(timeout=self._timeout) as http:
            r = await http.get(
                f"{self._base_url}/api/v1/jobs/{job_id}/runs",
                params={"limit": limit},
                headers=self._headers(),
            )
            r.raise_for_status()
            return r.json()

    async def get_job_and_run(
        self, *,
        job_id: str,
        run_id: str,
        tenant_id: str,
    ) -> tuple[dict[str, Any], dict[str, Any]]:
        """Fetch Job + Run in parallel for the retrigger handler.

        Raises JobNotFoundError if the Job lookup 404s; RunNotFoundError if the
        Run lookup 404s. If both 404, JobNotFoundError wins (the Run can't exist
        without the Job)."""
        async with httpx.AsyncClient(timeout=self._timeout) as http:
            job_resp, run_resp = await asyncio.gather(
                http.get(f"{self._base_url}/api/v1/jobs/{job_id}", headers=self._headers()),
                http.get(f"{self._base_url}/api/v1/jobs/{job_id}/runs/{run_id}", headers=self._headers()),
            )
            if job_resp.status_code == 404:
                raise JobNotFoundError(f"job {job_id!r} not found")
            if run_resp.status_code == 404:
                raise RunNotFoundError(f"run {run_id!r} not found for job {job_id!r}")
            job_resp.raise_for_status()
            run_resp.raise_for_status()
            return job_resp.json(), run_resp.json()

    async def start_run(
        self,
        *,
        job_id: str,
        idempotency_key: str,
        trigger: str = "schedule",
        run_id: str | None = None,
    ) -> str:
        """Returns the run_id (server returns 201 for new, also 201 for triggered transition)."""
        body: dict[str, Any] = {"trigger": trigger, "idempotency_key": idempotency_key}
        if run_id is not None:
            body["run_id"] = run_id
        async with httpx.AsyncClient(timeout=self._timeout) as http:
            r = await http.post(
                f"{self._base_url}/api/v1/jobs/{job_id}/runs",
                json=body, headers=self._headers(),
            )
            r.raise_for_status()
            return r.json()["run_id"]

    async def finish_run(
        self,
        *,
        job_id: str,
        run_id: str,
        status: str,
        exit_code: int | None,
        summary: dict[str, Any] | None,
    ) -> str:
        """Returns the run's current status (succeeded/failed/cancelled)."""
        body = {"status": status, "exit_code": exit_code, "summary": summary}
        async with httpx.AsyncClient(timeout=self._timeout) as http:
            r = await http.patch(
                f"{self._base_url}/api/v1/jobs/{job_id}/runs/{run_id}",
                json=body, headers=self._headers(),
            )
            r.raise_for_status()
            return r.json()["status"]

    async def log(self, *, job_id: str, run_id: str, line: str) -> None:
        """Best-effort log forward. Never raises (log failures don't fail handlers)."""
        try:
            async with httpx.AsyncClient(timeout=self._timeout) as http:
                await http.post(
                    f"{self._base_url}/api/v1/jobs/{job_id}/runs/{run_id}/logs",
                    json={"line": line}, headers=self._headers(),
                )
        except Exception:
            pass

    async def heartbeat(self, *, job_id: str) -> None:
        async with httpx.AsyncClient(timeout=self._timeout) as http:
            r = await http.post(
                f"{self._base_url}/api/v1/jobs/{job_id}/heartbeat",
                headers=self._headers(),
            )
            r.raise_for_status()
