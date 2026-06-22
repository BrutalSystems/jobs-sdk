"""The Job execution Policy — the single source of truth for both sides.

Producers (the SDK's `register_job`) construct a Policy and serialize it; the
service validates and stores it as an embedded subdocument. This class was once
defined twice — once in the SDK and once in the service — and the two silently
drifted (that is how `dispatch_mode` once ended up stored as None on the
consumer side). One definition here makes that drift impossible by construction."""

from __future__ import annotations

from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field


class Resources(BaseModel):
    cpu_request: str
    cpu_limit: str
    mem_request: str
    mem_limit: str


class Policy(BaseModel):
    # Reject unknown fields rather than silently dropping them. A producer on a
    # newer build may send a Policy field an older service doesn't know yet
    # (version skew). Default Pydantic would drop it silently. Fail loud (422 at
    # registration) instead so the skew surfaces immediately. NOTE: deploy the
    # service before any producer that sends a new field.
    model_config = ConfigDict(extra="forbid")

    execution_mode: str          # "in_process" | "container"
    concurrency: int = 1
    resources: Resources | None = None
    image: str | None = None
    command: list[str] | None = None
    env_from: list[str] = Field(default_factory=list)
    # ConfigMap-sourced env (vs env_from, which is Secret-sourced). Use for
    # non-sensitive config the pod needs as env vars — e.g. an S3 bucket/region.
    config_map_env_from: list[str] = Field(default_factory=list)
    timeout_seconds: int = 3600
    prefer_spot: bool = False
    node_arch: str = "any"       # "any" | "arm64" | "amd64"
    service_account: str | None = None
    orchestrator_overrides: dict[str, Any] = Field(default_factory=dict)
    # Route a triggered Run via direct k8s dispatch (default), or a queued path.
    # The trigger route reads this and routes to the right adapter.
    dispatch_mode: Literal["direct", "warm-queued", "cold-queued"] = "direct"
