"""
WS /api/v2/sessions/{session_id}/stream: a training session's snapshots and traffic, pushed.

Author: Lau Xiang Yang
Date Created: 18/11/2026
Last Modified: 18/11/2026

Description:
    The push transport of the multi-core sessions plan (Phase 3); session_stream.SessionStream does the
    work and contracts/v2/stream.py defines the messages. Query parameters:

    - traffic: true to receive the session's traffic frames as they are made (default false);
    - after_tick: only traffic frames after this tick (default -1, the newest kept);
    - snapshot_interval_ms: the least time between two snapshots, 100 to 5000 (default 250).

    The login is the same bearer token as the HTTP routes ("Authorization" header on the upgrade
    request). The stream is accepted first and then refused with an error message when the login is
    missing, so a client always learns why; a token that is unknown or expired is refused by the
    authentication middleware before the upgrade, as over HTTP. While the stream is open its token is
    checked again every session_stream.LOGIN_CHECK_S: once logged out or expired, the stream ends with
    ROLE_DENIED (4403), as HTTP then answers 401.

    These routes are kept apart from training_v2's router, whose dependencies read an HTTP Request.
"""

from __future__ import annotations

import asyncio
from typing import Awaitable, Callable

from fastapi import APIRouter, Depends, WebSocket

from src.auth.service import AccountsUnavailable, get_auth_service
from src.api.routes.training_v2 import get_actor_id, get_training_store, initialize_training_service
from src.persistence.training_store import TrainingStore, TrainingStoreError
from src.services.session_stream import SessionStream, clamp_interval_ms, close_code, error_message
from src.services.training_sessions import SessionServiceBase

router = APIRouter(prefix="/v2/sessions", tags=["training-v2"])


def stream_actor_id(websocket: WebSocket) -> str | None:
    """The authenticated actor of a stream, or None: the stream then refuses with ROLE_DENIED."""
    try:
        return get_actor_id(websocket)  # Reads only the connection's scope, as for a Request
    except TrainingStoreError:
        return None


def login_check_for(websocket: WebSocket, actor_id: str) -> Callable[[], Awaitable[bool | None]] | None:
    """For an open stream: whether its bearer token still belongs to actor_id (None when that cannot be
    told now). None when the connection has no token to check (a login given some other way)."""
    scheme, _, token = websocket.headers.get("authorization", "").partition(" ")
    token = token.strip()
    if scheme.lower() != "bearer" or not token:
        return None

    def lookup() -> tuple[bool, str | None]:
        service = get_auth_service()
        if service is None:
            return False, None
        return True, service.user_for_token(token)

    async def check() -> bool | None:
        try:
            configured, username = await asyncio.to_thread(lookup)
        except AccountsUnavailable:
            return None  # Not the same as a login gone: the stream goes on
        if not configured:
            return None
        return username == actor_id

    return check


async def stream_service(websocket: WebSocket, store: TrainingStore = Depends(get_training_store)) -> SessionServiceBase:
    return await initialize_training_service(websocket.app, store)


@router.websocket("/{session_id}/stream")
async def session_stream(websocket: WebSocket, session_id: str, traffic: bool = False, after_tick: int = -1,
                         snapshot_interval_ms: int | None = None,
                         actor_id: str | None = Depends(stream_actor_id),
                         service: SessionServiceBase = Depends(stream_service)) -> None:
    await websocket.accept()
    if actor_id is None:
        await websocket.send_text(error_message(session_id, "ROLE_DENIED", "Training API requires an authenticated actor"))
        await websocket.close(code=close_code("ROLE_DENIED"), reason="ROLE_DENIED")
        return
    stream = SessionStream(websocket, service, session_id, traffic=traffic, after_tick=after_tick,
                           snapshot_interval_ms=clamp_interval_ms(snapshot_interval_ms),
                           login_check=login_check_for(websocket, actor_id))
    with service.store.trace.span("stream.session", session_id=session_id, traffic=traffic,
                                  snapshot_interval_ms=round(stream.interval_s * 1000)) as fields:
        try:
            await stream.run()
        finally:
            fields.update(snapshots_sent=stream.snapshots_sent, frames_sent=stream.frames_sent)
