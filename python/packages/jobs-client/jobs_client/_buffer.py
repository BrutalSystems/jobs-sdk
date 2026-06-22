"""Append-only JSONL buffer for failed lifecycle calls.

The wrapper writes here when the Jobs service is unreachable; a future
drainer (V2) reads + replays. v1 only writes; drain comes later."""

from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any

from jobs_core import pod_env


class EventBuffer:
    def __init__(self, path: str | None = None):
        self._path = Path(
            path or os.environ.get(pod_env.BUFFER_PATH, pod_env.BUFFER_PATH_DEFAULT)
        )

    def write(self, event: dict[str, Any]) -> None:
        self._path.parent.mkdir(parents=True, exist_ok=True)
        with self._path.open("a") as f:
            f.write(json.dumps(event) + "\n")
