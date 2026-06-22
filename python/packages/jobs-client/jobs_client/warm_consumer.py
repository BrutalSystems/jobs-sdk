"""Warm-queued consumer entrypoint.

Boot sequence:
  1. Read env into WarmConsumerConfig
  2. Discover warm-queued Jobs for this owner via jobs-service
  3. Validate each has a Callable in HANDLERS
  4. Open AMQP connection, declare topology per Job, basic_consume
  5. Loop forever (reconnect with backoff on AMQPConnectionError)

Subsequent steps add the message-processing path. This task only
implements scaffolding + the validation that runs at boot."""

from __future__ import annotations

import asyncio
import logging
import os
import sys
from dataclasses import dataclass
from typing import Any

from jobs_client import HANDLERS
from jobs_client import observability as obs
from jobs_client.client import JobsClient
from jobs_core import EXCHANGE_NAME, dead_queue_name, pod_env, retry_queue_name, work_queue_name

logger = logging.getLogger(__name__)


@dataclass(frozen=True)
class WarmConsumerConfig:
    amqp_url: str
    jobs_service_url: str
    owner_service: str
    tenant_id: str
    prefetch_default: int = 1
    max_attempts: int = 5
    metrics_port: int = 9102
    liveness_staleness_seconds: int = 120

    @classmethod
    def from_env(cls) -> WarmConsumerConfig:
        def _required(name: str) -> str:
            v = os.environ.get(name)
            if not v:
                raise RuntimeError(f"{name} env var required by warm_consumer")
            return v

        return cls(
            amqp_url=_required("AMQP_URL"),
            jobs_service_url=_required(pod_env.SERVICE_URL),
            owner_service=_required("OWNER_SERVICE"),
            tenant_id=os.environ.get("WARM_TENANT_ID", "_org"),
            prefetch_default=int(os.environ.get("WARM_PREFETCH_DEFAULT", "1")),
            max_attempts=int(os.environ.get("WARM_MAX_ATTEMPTS", "5")),
            metrics_port=int(os.environ.get("METRICS_PORT", "9102")),
            liveness_staleness_seconds=int(os.environ.get("LIVENESS_STALENESS_SECONDS", "120")),
        )


async def discover_warm_jobs(
    *, client: JobsClient, owner_service: str,
) -> list[dict[str, Any]]:
    """Query jobs-service for warm-queued Jobs owned by this service."""
    return await client.list_jobs_filtered(
        owner_service=owner_service, dispatch_mode="warm-queued",
    )


def validate_handlers(jobs: list[dict[str, Any]]) -> None:
    """Boot-time check: every discovered warm Job has a Callable in HANDLERS
    that accepts **kwargs. Fail loud if not."""
    import inspect
    for job in jobs:
        name = job["name"]
        if name not in HANDLERS:
            raise RuntimeError(
                f"warm Job {name!r} registered but no handler registered in HANDLERS dict; "
                f"add `register_handler({name!r}, ...)` to your owner-service's "
                f"boot path before starting the warmworker"
            )
        handler = HANDLERS[name]
        if not callable(handler):
            raise RuntimeError(
                f"HANDLERS[{name!r}] is not callable: {type(handler).__name__}"
            )
        sig = inspect.signature(handler)
        has_var_kw = any(
            p.kind is inspect.Parameter.VAR_KEYWORD for p in sig.parameters.values()
        )
        if not has_var_kw:
            raise RuntimeError(
                f"handler for {name!r} must accept **kwargs (envelope args are variadic); "
                f"signature: {sig}"
            )


import inspect
import json
import time

import aio_pika

from jobs_client.exceptions import TransientError  # used by later tasks; safe to import now
from jobs_client.logging import bind_log_context  # SP6 contextvars helper


async def process_message(
    *,
    message: aio_pika.abc.AbstractIncomingMessage,
    exchange: aio_pika.abc.AbstractExchange,
    client: JobsClient,
    max_attempts: int,
    timeout_seconds: int,
) -> None:
    """Handle one AMQP delivery. Final version: happy path + TransientError
    retry + DLQ on max attempts + handler timeout + generic exception.

    Sync handlers run on a thread via asyncio.to_thread so the wait_for can
    actually cancel them — a synchronous blocking call inside the event loop
    would prevent the TimeoutError from being raised."""
    try:
        env = json.loads(message.body)
        job_id = env["job_id"]
        run_id = env["run_id"]
        job_name = env["job_name"]
    except (json.JSONDecodeError, KeyError, TypeError) as exc:
        logger.error("malformed warm envelope, dropping: %s", exc)
        obs.queue_consume_total.labels(
            job_name="_unparsed", dispatch_mode="warm-queued", outcome="dropped",
        ).inc()
        await message.ack()
        return
    args = env.get("args", {})

    handler = HANDLERS.get(job_name)
    if handler is None:
        logger.error("no handler for %s; acking-and-dropping", job_name)
        obs.queue_consume_total.labels(
            job_name=job_name, dispatch_mode="warm-queued", outcome="dropped",
        ).inc()
        await message.ack()
        return

    with bind_log_context(
        run_id=run_id, job_id=job_id,
        tenant_id=env.get("tenant_id", ""),
        owner_service=env.get("owner_service", ""),
    ):
        await client.start_run(
            job_id=job_id, run_id=run_id,
            idempotency_key=run_id, trigger="queue",
        )

        started = time.monotonic()
        summary: dict | None = None
        status = "succeeded"
        exit_code = 0
        timed_out = False

        try:
            if inspect.iscoroutinefunction(handler):
                coro = handler(**args)
            else:
                # Run sync handler on a thread so wait_for can cancel it.
                coro = asyncio.to_thread(handler, **args)
            try:
                result = await asyncio.wait_for(coro, timeout=timeout_seconds)
            except TimeoutError:
                timed_out = True
                status = "failed"
                exit_code = 1
                summary = {"timeout": True, "timeout_seconds": timeout_seconds}
                # Skip exception branches below — Run terminates here.
                raise _TimeoutExit() from None
            if isinstance(result, dict):
                summary = result
        except _TimeoutExit:
            pass  # status/summary already set
        except TransientError as exc:
            attempt = int(message.headers.get("attempt", env.get("attempt", 1)))
            if attempt < max_attempts:
                await _publish_to_retry(
                    exchange=exchange, env=env,
                    attempt=attempt + 1, reason=str(exc),
                )
                obs.queue_consume_total.labels(
                    job_name=job_name, dispatch_mode="warm-queued", outcome="retry",
                ).inc()
                await message.ack()
                return
            await _publish_to_dlq(
                exchange=exchange, env=env,
                attempts=attempt, reason=str(exc),
            )
            status = "failed"
            exit_code = 1
            summary = {"error": str(exc), "dlq": True, "attempts": attempt}
        except Exception as exc:
            logger.exception("handler failed: %s", exc)
            status = "failed"
            exit_code = 1
            summary = {"error": str(exc)}

        duration_ms = int((time.monotonic() - started) * 1000)
        # Handler-duration histogram (seconds), labelled by handler outcome.
        if timed_out:
            handler_outcome = "timeout"
        elif status == "failed":
            handler_outcome = "failed"
        else:
            handler_outcome = "succeeded"
        obs.warm_handler_duration_seconds.labels(
            job_name=job_name, outcome=handler_outcome,
        ).observe(duration_ms / 1000.0)
        # Consume outcome: dlq if this was a max-attempts DLQ disposition, else ack.
        attempt_now = int(message.headers.get("attempt", env.get("attempt", 1)))
        consume_outcome = "dlq" if (summary or {}).get("dlq") else "ack"
        obs.queue_consume_total.labels(
            job_name=job_name, dispatch_mode="warm-queued", outcome=consume_outcome,
        ).inc()
        obs.queue_attempt_count.labels(job_name=job_name, dispatch_mode="warm-queued").observe(attempt_now)
        await client.finish_run(
            job_id=job_id, run_id=run_id,
            status=status, exit_code=exit_code, summary=summary,
        )
        await message.ack()


class _TimeoutExit(Exception):
    """Internal sentinel: handler-side timeout already set status/summary;
    skip the catch-all generic-exception path so we don't clobber it."""


async def _publish_to_dlq(
    *,
    exchange: aio_pika.abc.AbstractExchange,
    env: dict,
    attempts: int,
    reason: str,
) -> None:
    """Publish the envelope to the per-Job DLQ when retries are exhausted."""
    work_queue = f"jobs.{env['owner_service']}.{env['job_name']}"
    dead_queue = f"{work_queue}.dead"
    body = json.dumps({**env, "attempt": attempts}).encode("utf-8")
    msg = aio_pika.Message(
        body=body,
        delivery_mode=aio_pika.DeliveryMode.PERSISTENT,
        content_type="application/json",
        message_id=env["run_id"],
        headers={"attempt": attempts, "last_error": reason[:1024], "dlq": True},
    )
    await exchange.publish(msg, routing_key=dead_queue)


async def _publish_to_retry(
    *,
    exchange: aio_pika.abc.AbstractExchange,
    env: dict,
    attempt: int,
    reason: str,
) -> None:
    """Publish the envelope to the retry queue with attempt+1 in headers.
    Queue names come from the shared jobs_core helpers — the single source of
    truth for topology, so the SDK and service can't disagree."""
    work_queue = work_queue_name(
        owner_service=env["owner_service"], job_name=env["job_name"], dispatch_mode="warm-queued",
    )
    retry_queue = retry_queue_name(work_queue)
    body = json.dumps({**env, "attempt": attempt}).encode("utf-8")
    msg = aio_pika.Message(
        body=body,
        delivery_mode=aio_pika.DeliveryMode.PERSISTENT,
        content_type="application/json",
        message_id=env["run_id"],
        headers={"attempt": attempt, "last_error": reason[:1024]},
    )
    await exchange.publish(msg, routing_key=retry_queue)


async def _poll_warm_queue_depths(
    *, connection, jobs, owner_service, heartbeat, interval: int, stop_event: asyncio.Event,
) -> None:
    """Every `interval`s, read each warm Job's work + dead queue depth into the
    depth gauges, and bump the heartbeat so an idle-but-healthy worker stays
    /healthz-green.

    Uses its OWN channel (opened per cycle from `connection`), NOT the consumers'
    channel: a passive declare of a not-yet-created queue raises NOT_FOUND and
    the broker CLOSES the channel, so sharing the consumers' channel would kill
    them. On a closed channel we just reopen for the next read."""
    while not stop_event.is_set():
        channel = await connection.channel()
        try:
            for job in jobs:
                work = work_queue_name(
                    owner_service=owner_service, job_name=job["name"], dispatch_mode="warm-queued",
                )
                dead = dead_queue_name(work)
                for qname, gauge in (
                    (work, obs.queue_pending_messages),
                    (dead, obs.queue_dlq_messages),
                ):
                    try:
                        q = await channel.declare_queue(qname, passive=True)
                        gauge.labels(
                            job_name=job["name"], dispatch_mode="warm-queued",
                        ).set(q.declaration_result.message_count)
                    except Exception as exc:  # queue not created yet → NOT_FOUND closes the channel
                        logger.debug("warm queue-depth poll skipped for %s: %s", qname, exc)
                        if channel.is_closed:
                            channel = await connection.channel()
        finally:
            if not channel.is_closed:
                await channel.close()
        heartbeat.beat()
        await asyncio.sleep(interval)


async def _consume_one_job(
    *,
    channel: aio_pika.abc.AbstractChannel,
    job: dict,
    client: JobsClient,
    cfg: WarmConsumerConfig,
    retry_ttl_ms: int,
    stop_event: asyncio.Event,
    heartbeat: obs.Heartbeat,
) -> None:
    """Declare topology for one Job, open its consumer iterator, dispatch
    each delivery to `process_message` as its own asyncio task so prefetch>1
    yields real concurrency."""
    policy = job.get("policy", {})
    timeout_seconds = int(policy.get("timeout_seconds", 3600))
    concurrency = int(policy.get("concurrency", cfg.prefetch_default))

    work_queue = work_queue_name(
        owner_service=cfg.owner_service, job_name=job["name"], dispatch_mode="warm-queued",
    )
    retry_queue = retry_queue_name(work_queue)
    dead_queue = dead_queue_name(work_queue)

    exchange = await channel.declare_exchange(
        EXCHANGE_NAME, aio_pika.ExchangeType.TOPIC, durable=True,
    )
    work_q = await channel.declare_queue(work_queue, durable=True)
    retry_q = await channel.declare_queue(
        retry_queue, durable=True,
        arguments={
            "x-message-ttl": retry_ttl_ms,
            "x-dead-letter-exchange": EXCHANGE_NAME,
            "x-dead-letter-routing-key": work_queue,
        },
    )
    dead_q = await channel.declare_queue(dead_queue, durable=True)
    await work_q.bind(exchange, routing_key=work_queue)
    await retry_q.bind(exchange, routing_key=retry_queue)
    await dead_q.bind(exchange, routing_key=dead_queue)

    await channel.set_qos(prefetch_count=concurrency)

    async with work_q.iterator() as it:
        in_flight: set[asyncio.Task] = set()
        async for message in it:
            if stop_event.is_set():
                break
            task = asyncio.create_task(
                process_message(
                    message=message, exchange=exchange,
                    client=client, max_attempts=cfg.max_attempts,
                    timeout_seconds=timeout_seconds,
                )
            )
            in_flight.add(task)
            task.add_done_callback(in_flight.discard)
            heartbeat.beat()
        if in_flight:
            await asyncio.gather(*in_flight, return_exceptions=True)


async def _run_consumer_iteration(
    *,
    cfg: WarmConsumerConfig,
    jobs: list[dict],
    client: JobsClient,
    stop_event: asyncio.Event,
    heartbeat: obs.Heartbeat,
) -> None:
    """One robust-connection lifetime. aio_pika.connect_robust auto-reconnects
    on transient drops; this iteration ends only when stop_event fires OR a
    non-recoverable error escapes."""
    retry_ttl_ms = int(os.environ.get("WARM_RETRY_TTL_MS", "30000"))
    connection = await aio_pika.connect_robust(cfg.amqp_url)
    obs.queue_connection_up.labels(endpoint="warmworker").set(1)
    try:
        channel = await connection.channel()
        consumer_tasks = [
            asyncio.create_task(_consume_one_job(
                channel=channel, job=job, client=client, cfg=cfg,
                retry_ttl_ms=retry_ttl_ms, stop_event=stop_event,
                heartbeat=heartbeat,
            ))
            for job in jobs
        ]
        poller_task = asyncio.create_task(_poll_warm_queue_depths(
            connection=connection, jobs=jobs, owner_service=cfg.owner_service,
            heartbeat=heartbeat, interval=10, stop_event=stop_event,
        ))
        stop_task = asyncio.create_task(stop_event.wait())
        try:
            done, pending = await asyncio.wait(
                [stop_task, *consumer_tasks, poller_task],
                return_when=asyncio.FIRST_COMPLETED,
            )
            for t in done:
                if t in consumer_tasks and t.exception():
                    raise t.exception()
        finally:
            stop_event.set()
            for t in [*consumer_tasks, poller_task]:
                if not t.done():
                    t.cancel()
            await asyncio.gather(*consumer_tasks, poller_task, return_exceptions=True)
    finally:
        await connection.close()


async def _run(cfg: WarmConsumerConfig) -> None:
    """Main async loop. Discover Jobs, validate handlers, run consumer
    iterations. aio_pika.connect_robust handles transient broker drops;
    non-recoverable errors trigger our own reconnect-with-backoff loop."""
    client = JobsClient.from_env(
        owner_service=cfg.owner_service, tenant_id=cfg.tenant_id,
    )
    jobs = await discover_warm_jobs(client=client, owner_service=cfg.owner_service)
    if not jobs:
        logger.warning("no warm-queued Jobs registered for owner %s; exiting", cfg.owner_service)
        return
    validate_handlers(jobs)

    heartbeat = obs.Heartbeat()
    obs.start_metrics_server(
        port=cfg.metrics_port, heartbeat=heartbeat,
        staleness_seconds=cfg.liveness_staleness_seconds,
    )

    backoff = 1.0
    while True:
        # Fresh stop_event each attempt. _run_consumer_iteration's finally
        # always calls stop_event.set(), so reusing a single event would make
        # this loop exit after the first crash instead of reconnecting.
        stop_event = asyncio.Event()
        try:
            await _run_consumer_iteration(
                cfg=cfg, jobs=jobs, client=client, stop_event=stop_event,
                heartbeat=heartbeat,
            )
            break
        except Exception as exc:
            obs.queue_connection_up.labels(endpoint="warmworker").set(0)
            # Beat once per backoff iteration (cap 30s) so a broker blip doesn't
            # trip the 120s liveness window while we're alive and retrying. If a
            # single connect_robust() instead blocks past the staleness window
            # (e.g. DNS blackhole), /healthz goes stale and k8s restarts the pod
            # — the desired outcome for a worker that can't reach the broker.
            heartbeat.beat()
            logger.warning("consumer iteration crashed, reconnecting in %ss: %s", backoff, exc)
            await asyncio.sleep(backoff)
            backoff = min(backoff * 2, 30)


class _ContextDefaultsFormatter(logging.Formatter):
    """Formatter that supplies empty defaults for the contextvar log keys so
    records emitted outside a bind_log_context block don't KeyError on
    `%(run_id)s` etc."""

    _DEFAULTS = ("run_id", "job_id", "tenant_id", "owner_service", "trace_id")

    def format(self, record: logging.LogRecord) -> str:
        for k in self._DEFAULTS:
            if not hasattr(record, k):
                setattr(record, k, "")
        return super().format(record)


def _import_handler_modules() -> None:
    """Populate HANDLERS by importing the owner-service's handler modules.

    The warmworker runs `python -m jobs_client.warm_consumer`, which does NOT
    import the owner-service app (the module where the warm handlers register
    at import time). Without this, HANDLERS is empty and
    `validate_handlers()` fails at boot. `WARM_HANDLER_MODULES` is a comma-
    separated list of importable modules whose import registers the handlers."""
    import importlib
    for mod in os.environ.get("WARM_HANDLER_MODULES", "").split(","):
        mod = mod.strip()
        if mod:
            importlib.import_module(mod)


def main() -> None:
    """Process entrypoint. Sets up JSON-shaped logging with contextvar bindings,
    reads config from env, runs the consumer loop."""
    from jobs_client.logging import install_context_filter

    fmt = '{"ts":"%(asctime)s","level":"%(levelname)s","logger":"%(name)s","msg":"%(message)s","run_id":"%(run_id)s","job_id":"%(job_id)s","tenant_id":"%(tenant_id)s"}'
    handler = logging.StreamHandler(sys.stderr)
    handler.setFormatter(_ContextDefaultsFormatter(fmt))
    root = logging.getLogger()
    root.addHandler(handler)
    root.addFilter(install_context_filter())
    root.setLevel(logging.INFO)

    _import_handler_modules()
    cfg = WarmConsumerConfig.from_env()
    try:
        asyncio.run(_run(cfg))
    except KeyboardInterrupt:
        sys.exit(0)


if __name__ == "__main__":
    main()
