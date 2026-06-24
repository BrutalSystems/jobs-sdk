"""Boot-time warm-job discovery should ride out the cold-start window.

On a simultaneous cold start the whole stack boots at once, so jobs-service
can briefly be unreachable or return 401 (its JWKS fetch from the auth service
hasn't warmed yet). The warm consumer's initial discovery runs before the
resilient AMQP reconnect loop and before the liveness server, so without a
retry here a transient failure crashes the process. These tests pin the retry
behaviour: retry transient failures with backoff, fail fast on real client
errors, and give up after a bounded number of attempts."""

from __future__ import annotations

import httpx
import pytest

from jobs_client import warm_consumer


def _http_error(status: int) -> httpx.HTTPStatusError:
    request = httpx.Request("GET", "http://jobs-service/api/v1/jobs")
    response = httpx.Response(status, request=request)
    return httpx.HTTPStatusError(str(status), request=request, response=response)


class _FakeClient:
    """Duck-typed JobsClient: fails `fail_times` calls, then returns a job list."""

    def __init__(self, *, fail_times: int, error: Exception) -> None:
        self._fail_times = fail_times
        self._error = error
        self.calls = 0

    async def list_jobs_filtered(self, *, owner_service: str, dispatch_mode: str):
        self.calls += 1
        if self.calls <= self._fail_times:
            raise self._error
        return [{"name": "demo.job", "owner_service": owner_service, "dispatch_mode": dispatch_mode}]


@pytest.fixture(autouse=True)
def _no_sleep(monkeypatch: pytest.MonkeyPatch) -> None:
    async def _instant(_seconds: float) -> None:
        return None

    monkeypatch.setattr(warm_consumer.asyncio, "sleep", _instant)


async def test_retries_transient_401_then_succeeds() -> None:
    client = _FakeClient(fail_times=2, error=_http_error(401))

    jobs = await warm_consumer.discover_warm_jobs(client=client, owner_service="sai-api")

    assert client.calls == 3
    assert jobs[0]["name"] == "demo.job"


async def test_retries_connection_error_then_succeeds() -> None:
    err = httpx.ConnectError("connection refused")
    client = _FakeClient(fail_times=1, error=err)

    jobs = await warm_consumer.discover_warm_jobs(client=client, owner_service="sai-api")

    assert client.calls == 2
    assert jobs[0]["name"] == "demo.job"


async def test_does_not_retry_real_client_error() -> None:
    client = _FakeClient(fail_times=1, error=_http_error(400))

    with pytest.raises(httpx.HTTPStatusError):
        await warm_consumer.discover_warm_jobs(client=client, owner_service="sai-api")

    assert client.calls == 1  # 400 is not transient — fail fast, no retry


async def test_gives_up_after_max_attempts() -> None:
    client = _FakeClient(fail_times=99, error=_http_error(401))

    with pytest.raises(httpx.HTTPStatusError):
        await warm_consumer.discover_warm_jobs(
            client=client, owner_service="sai-api", max_attempts=3,
        )

    assert client.calls == 3
