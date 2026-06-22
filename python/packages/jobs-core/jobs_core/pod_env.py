"""The JOBS_* env-var contract injected into spawned pods.

When the service dispatches a Run it injects these env vars into the pod
(see the manifest builder); the wrapper running inside the pod reads them back
to know what to execute and how to report lifecycle. Before extraction both
sides used bare string literals ("JOBS_RUN_ID", …) with no shared definition —
a rename on one side would silently break the other. These constants are the
contract; the producer and the wrapper both reference them.

Keep the *string values* stable: they cross the process boundary into a running
pod, so changing a value is a wire-breaking change, not a refactor."""

from __future__ import annotations

# --- Identity of the run (producer always sets these) ---
JOB_ID = "JOBS_JOB_ID"
RUN_ID = "JOBS_RUN_ID"
TENANT_ID = "JOBS_TENANT_ID"
OWNER_SERVICE = "JOBS_OWNER_SERVICE"

# Pod UID via the k8s downward API, so a pod restart reuses the same UID.
# The local-subprocess adapter resolves this to a synthetic per-run value.
POD_UID = "POD_UID"

# --- Callback + payload (producer sets conditionally) ---
# Where the wrapper reports lifecycle back to. Omitted in setups with no
# reachable service URL.
SERVICE_URL = "JOBS_SERVICE_URL"
# JSON-encoded per-trigger handler args. The wrapper decodes it and also
# explodes it into JOBS_ARG_* vars (see ARG_PREFIX).
HANDLER_ARGS = "JOBS_HANDLER_ARGS"
# JSON-encoded argv for the handler the pod should run.
HANDLER_CMD = "JOBS_HANDLER_CMD"

# --- Read by the wrapper, set on scheduled/cron paths ---
# Human-facing job name, when the dispatch path carries one.
NAME = "JOBS_NAME"

# --- Derived by the wrapper for the handler subprocess ---
# Each key of HANDLER_ARGS is re-exported as JOBS_ARG_<UPPER_KEY> for handlers
# that read config from the environment rather than argv.
ARG_PREFIX = "JOBS_ARG_"

# --- Lifecycle event buffer (read by the SDK's event buffer) ---
# Filesystem path the wrapper streams lifecycle events to as a fallback.
BUFFER_PATH = "JOBS_BUFFER_PATH"
BUFFER_PATH_DEFAULT = "/var/run/jobs-buffer.jsonl"

# --- m2m auth key (service puts it in the pod env; the SDK reads it to mint
# tokens back to the service). Part of the contract because both sides must
# agree on the name. Neutral; was "SAI_JWT_PRIVATE_KEY" pre-extraction. ---
JWT_PRIVATE_KEY = "JOBS_JWT_PRIVATE_KEY"


def arg_env_name(key: str) -> str:
    """Map a handler-arg key to its JOBS_ARG_* env var name."""
    return f"{ARG_PREFIX}{key.upper()}"
