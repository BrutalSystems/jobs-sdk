"""Metrics + heartbeat-backed health server for the warmworker.

The warmworker (`jobs_client.warm_consumer`) is a pure client and cannot import
the service internals, so this DUPLICATES the cold dispatcher's observability
module on the consumer's own prometheus registry. The metric NAMES match
jobs-service's so Prometheus aggregates across both targets. Only the
`connection_up` gauge carries an explicit `endpoint` label — it's a per-process
liveness signal that must distinguish warm vs cold within a single query; the
counters/histograms rely on Prometheus's automatic per-target `job`/`instance`
labels instead, so they deliberately don't add a process label. /healthz is
heartbeat-checked because the HTTP server runs in its own thread and answers
/metrics even if the asyncio loop hangs."""

from __future__ import annotations

import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from prometheus_client import CollectorRegistry, Counter, Gauge, Histogram, generate_latest

REGISTRY = CollectorRegistry()

queue_consume_total = Counter(
    "jobs_queue_consume_total", "Queue consumer outcomes",
    labelnames=("job_name", "dispatch_mode", "outcome"),
    registry=REGISTRY,
)
queue_attempt_count = Histogram(
    "jobs_queue_attempt_count", "Attempt number at terminal disposition",
    labelnames=("job_name", "dispatch_mode"),
    registry=REGISTRY,
)
queue_connection_up = Gauge(
    "jobs_queue_connection_up", "1 if the queue connection is up, 0 if reconnecting",
    labelnames=("endpoint",),
    registry=REGISTRY,
)
queue_pending_messages = Gauge(
    "jobs_queue_pending_messages", "Messages waiting in each work queue",
    labelnames=("job_name", "dispatch_mode"),
    registry=REGISTRY,
)
queue_dlq_messages = Gauge(
    "jobs_queue_dlq_messages", "Messages in each dead-letter queue",
    labelnames=("job_name", "dispatch_mode"),
    registry=REGISTRY,
)
warm_handler_duration_seconds = Histogram(
    "jobs_warm_handler_duration_seconds", "Time inside the warm handler callable",
    labelnames=("job_name", "outcome"),
    registry=REGISTRY,
)


def render() -> bytes:
    return generate_latest(REGISTRY)


class Heartbeat:
    """Monotonic last-progress timestamp. Bumped by the async loop, read by the
    HTTP thread; a Lock keeps the cross-thread read/write tidy."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._last = time.monotonic()

    def beat(self) -> None:
        with self._lock:
            self._last = time.monotonic()

    def seconds_since(self) -> float:
        with self._lock:
            return time.monotonic() - self._last


def start_metrics_server(
    *, port: int, heartbeat: Heartbeat, staleness_seconds: int,
) -> ThreadingHTTPServer:
    """Daemon-thread HTTP server serving /metrics and /healthz on `port`.
    /healthz is 200 while the heartbeat is fresh, 503 once stale."""

    class _Handler(BaseHTTPRequestHandler):
        def do_GET(self):  # noqa: N802 — stdlib-mandated name
            if self.path == "/metrics":
                body = render()
                self.send_response(200)
                self.send_header("Content-Type", "text/plain; version=0.0.4")
                self.end_headers()
                self.wfile.write(body)
            elif self.path == "/healthz":
                stale = heartbeat.seconds_since() >= staleness_seconds
                self.send_response(503 if stale else 200)
                self.send_header("Content-Type", "text/plain")
                self.end_headers()
                self.wfile.write(b"stale\n" if stale else b"ok\n")
            else:
                self.send_response(404)
                self.end_headers()

        def log_message(self, *args):  # silence per-request stderr noise
            pass

    server = ThreadingHTTPServer(("0.0.0.0", port), _Handler)
    server.daemon_threads = True
    threading.Thread(target=server.serve_forever, name="metrics-http", daemon=True).start()
    return server
