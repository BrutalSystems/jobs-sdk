"""m2m JWT minting for talking to the Jobs service.

Signs with the consumer's JWT keypair so the Jobs service validates via JWKS the
same way it would for any token that keypair issues."""

from __future__ import annotations

import base64
import hashlib
import os
from datetime import UTC, datetime, timedelta

import jwt as pyjwt
from cryptography.hazmat.primitives import serialization

from jobs_core import pod_env


def _get_private_key() -> str:
    key = os.environ.get(pod_env.JWT_PRIVATE_KEY)
    if not key:
        raise RuntimeError(f"{pod_env.JWT_PRIVATE_KEY} env var required for m2m token minting")
    return key


def _compute_kid_from_pem(private_key_pem: str) -> str:
    """First 16 chars of URL-safe base64 SHA-256 of the public-key DER.

    Must match the kid the issuer computes for the same keypair, so the JWKS
    endpoint the validator trusts finds the matching key when validating
    SDK-minted tokens."""
    private = serialization.load_pem_private_key(private_key_pem.encode(), password=None)
    public_der = private.public_key().public_bytes(
        encoding=serialization.Encoding.DER,
        format=serialization.PublicFormat.SubjectPublicKeyInfo,
    )
    return base64.urlsafe_b64encode(hashlib.sha256(public_der).digest()).decode().rstrip("=")[:16]


def mint_m2m_token(
    *,
    owner_service: str,
    tenant_id: str,
    ttl_seconds: int = 3600,
) -> str:
    """Mint a single-use JWT for talking to the Jobs service.

    scope is set to "admin" iff tenant_id == "_org" (cluster-wide infra).
    The `kid` header is computed from the public key so PyJWKClient on the
    receiving side resolves the right JWKS entry."""
    private_key = _get_private_key()
    now = datetime.now(UTC)
    scope = "admin" if tenant_id == "_org" else "tenant"
    claims = {
        "iat": int(now.timestamp()),
        "exp": int((now + timedelta(seconds=ttl_seconds)).timestamp()),
        "sub": owner_service,
        "tenant_id": tenant_id,
        "scope": scope,
    }
    return pyjwt.encode(
        claims,
        private_key,
        algorithm="RS256",
        headers={"kid": _compute_kid_from_pem(private_key)},
    )


class TokenProvider:
    """Callable that returns a cached m2m token, refreshing before expiry."""

    def __init__(self, *, owner_service: str, tenant_id: str, ttl_seconds: int = 3600):
        self._owner_service = owner_service
        self._tenant_id = tenant_id
        self._ttl_seconds = ttl_seconds
        self._cached_token: str | None = None
        self._cached_expiry: datetime | None = None

    def __call__(self) -> str:
        now = datetime.now(UTC)
        # Refresh if token is missing or within 60s of expiry.
        if self._cached_token is None or self._cached_expiry is None or self._cached_expiry - now < timedelta(seconds=60):
            self._cached_token = mint_m2m_token(
                owner_service=self._owner_service,
                tenant_id=self._tenant_id,
                ttl_seconds=self._ttl_seconds,
            )
            self._cached_expiry = now + timedelta(seconds=self._ttl_seconds)
        return self._cached_token
