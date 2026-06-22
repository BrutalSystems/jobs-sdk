"""Contract: the Job execution Policy schema.

Policy is constructed by the producer (register_job) and validated/stored by the
service. Both import the SAME class from jobs_core, so they cannot disagree on
fields — but a consumer pinned to an older jobs-core version still needs the wire
shape to be stable. This test pins the field set, defaults, and the fail-loud
(extra="forbid") behavior that surfaces version skew at registration time."""

from __future__ import annotations

import pytest
from pydantic import ValidationError

from jobs_core import Policy, Resources


def test_policy_field_set_is_frozen():
    assert set(Policy.model_fields) == {
        "execution_mode", "concurrency", "resources", "image", "command",
        "env_from", "config_map_env_from", "timeout_seconds", "prefer_spot",
        "node_arch", "service_account", "orchestrator_overrides", "dispatch_mode",
    }


def test_policy_defaults_are_frozen():
    p = Policy(execution_mode="container")
    assert p.concurrency == 1
    assert p.resources is None
    assert p.env_from == []
    assert p.config_map_env_from == []
    assert p.timeout_seconds == 3600
    assert p.prefer_spot is False
    assert p.node_arch == "any"
    assert p.dispatch_mode == "direct"


def test_policy_dispatch_modes_are_frozen():
    for mode in ("direct", "warm-queued", "cold-queued"):
        assert Policy(execution_mode="container", dispatch_mode=mode).dispatch_mode == mode
    with pytest.raises(ValidationError):
        Policy(execution_mode="container", dispatch_mode="bogus")


def test_policy_rejects_unknown_fields():
    # extra="forbid": a producer on a newer build sending an unknown field must
    # 422 at registration, not have the field silently dropped (version skew).
    with pytest.raises(ValidationError):
        Policy.model_validate({"execution_mode": "container", "unknown_field": 1})


def test_resources_field_set_is_frozen():
    assert set(Resources.model_fields) == {
        "cpu_request", "cpu_limit", "mem_request", "mem_limit",
    }
