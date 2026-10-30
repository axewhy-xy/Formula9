"""Reads "Authorization: Bearer <token>" on every request and records who is logged in.

Routes then read request.user (request.scope["user"]); training_v2.get_actor_id relies on this.
"""

from __future__ import annotations

from typing import Callable

from starlette.authentication import AuthCredentials, AuthenticationBackend, AuthenticationError, SimpleUser
from starlette.concurrency import run_in_threadpool
from starlette.requests import HTTPConnection
from starlette.responses import JSONResponse

from src.auth.service import AccountsUnavailable, AuthService


class SessionExpired(AuthenticationError):
    """A token was sent but is unknown or past its expiry, so the person must log in again."""


class TokenAuthBackend(AuthenticationBackend):
    def __init__(self, get_service: Callable[[], AuthService | None]) -> None:
        self._get_service = get_service

    async def authenticate(self, conn: HTTPConnection) -> tuple[AuthCredentials, SimpleUser] | None:
        scheme, _, token = conn.headers.get("authorization", "").partition(" ")
        token = token.strip()
        if scheme.lower() != "bearer" or not token:
            return None
        def lookup() -> tuple[bool, str | None]:
            # The first call builds the MongoDB client (SRV DNS lookup, shared lock), so it blocks too
            service = self._get_service()
            if service is None:
                return False, None
            return True, service.user_for_token(token)

        try:
            # Database work blocks, so keep all of it off the event loop
            configured, username = await run_in_threadpool(lookup)
        except AccountsUnavailable as error:
            # The token could not be checked, which is not the same as an invalid token. Treating it
            # as anonymous would answer 403, which clients take as final and so drop a command the
            # backend may already have accepted; answer a retryable 503 instead.
            raise AuthenticationError(error.detail) from error
        if not configured:
            return None
        if username is None:
            # Same reasoning: a distinct 401 lets clients keep pending work and send the person back
            # to the login page, instead of reading an anonymous 403 as a final rejection.
            raise SessionExpired("Your login has expired. Log in again to continue.")
        return AuthCredentials(["authenticated"]), SimpleUser(username)


def auth_error_response(_: HTTPConnection, error: AuthenticationError) -> JSONResponse:
    """AuthenticationMiddleware on_error handler. Both cases are retryable once resolved."""
    if isinstance(error, SessionExpired):
        return JSONResponse(status_code=401, content={"error": {
            "code": "session_expired", "message": str(error), "retryable": True,
        }})
    # The account database could not be reached
    return JSONResponse(status_code=503, content={"error": {
        "code": "accounts_unavailable", "message": str(error), "retryable": True,
    }})
