"""Contract: the JOBS_* pod-env variable names.

These strings cross the process boundary into a spawned pod — the service
injects them, the SDK wrapper reads them. Changing a *value* here is a
wire-breaking change that desyncs every deployed consumer, NOT a refactor.
This test pins the exact strings so such a change fails CI loudly.

If you must change one, it's a coordinated breaking release: bump the major
version and migrate all consumers in lockstep."""

from __future__ import annotations

from jobs_core import pod_env


def test_pod_env_var_names_are_frozen():
    assert pod_env.JOB_ID == "JOBS_JOB_ID"
    assert pod_env.RUN_ID == "JOBS_RUN_ID"
    assert pod_env.TENANT_ID == "JOBS_TENANT_ID"
    assert pod_env.OWNER_SERVICE == "JOBS_OWNER_SERVICE"
    assert pod_env.POD_UID == "POD_UID"
    assert pod_env.SERVICE_URL == "JOBS_SERVICE_URL"
    assert pod_env.HANDLER_ARGS == "JOBS_HANDLER_ARGS"
    assert pod_env.HANDLER_CMD == "JOBS_HANDLER_CMD"
    assert pod_env.NAME == "JOBS_NAME"
    assert pod_env.ARG_PREFIX == "JOBS_ARG_"
    assert pod_env.BUFFER_PATH == "JOBS_BUFFER_PATH"
    assert pod_env.BUFFER_PATH_DEFAULT == "/var/run/jobs-buffer.jsonl"
    assert pod_env.JWT_PRIVATE_KEY == "JOBS_JWT_PRIVATE_KEY"


def test_arg_env_name_derivation_is_frozen():
    # The wrapper explodes handler args into JOBS_ARG_<UPPER> for handlers that
    # read config from the environment. Pin the exact transform.
    assert pod_env.arg_env_name("limit") == "JOBS_ARG_LIMIT"
    assert pod_env.arg_env_name("tenant_id") == "JOBS_ARG_TENANT_ID"
