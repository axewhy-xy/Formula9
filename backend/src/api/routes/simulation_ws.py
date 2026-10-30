"""
Thin WebSocket transport for session-owned trajectory playback.

Author: Lau Xiang Yang, CC
Date Created: 12/09/2026
Last Modified: 20/09/2026

Description:
    This module defines a FastAPI WebSocket route for handling simulation sessions. 
    It allows clients to connect to a specific simulation session via WebSocket, 
    receive real-time updates, and send control messages to manipulate the simulation.

    The WebSocket route ensures that control messages are validated and targeted 
    correctly to the intended simulation session. It also manages the subscription 
    of clients to receive broadcasted events from the simulation session.

"""

from typing import Any

import anyio
from fastapi import APIRouter, WebSocket, WebSocketDisconnect

from src.core.errors import SessionNotFoundError
from src.services.session_manager import session_manager


router = APIRouter(tags=["simulation websocket"])


def _message_type(message: Any) -> str:
    if not isinstance(message, dict):
        return ""
    return str(message.get("message_type") or message.get("type") or "").upper()


async def _send_control_error(
    websocket: WebSocket,
    simulation_id: str,
    message: Any,
    context: str,
) -> None:
    session = session_manager.get_session(simulation_id)
    message_type = _message_type(message)
    await websocket.send_json(
        session.stream.error_message(
            simulation_id,
            "UNSUPPORTED_CONTROL",
            f"Control '{message_type}' is not valid {context}.",
            {"received": message},
        )
    )


async def _control_targets_session(
    websocket: WebSocket,
    simulation_id: str,
    message: Any,
) -> bool:
    """Reject controls whose envelope does not target this WebSocket session."""

    received_simulation_id = (
        message.get("simulation_id") if isinstance(message, dict) else None
    )
    if received_simulation_id == simulation_id:
        return True

    session = session_manager.get_session(simulation_id)
    await websocket.send_json(
        session.stream.error_message(
            simulation_id,
            "SIMULATION_ID_MISMATCH",
            "Control message does not target this simulation session.",
            {
                "expected_simulation_id": simulation_id,
                "received_simulation_id": received_simulation_id,
            },
        )
    )
    return False


@router.websocket("/ws/simulations/{simulation_id}")
async def simulation_socket(websocket: WebSocket, simulation_id: str) -> None:
    await websocket.accept()
    try:
        session = session_manager.get_session(simulation_id)
    except SessionNotFoundError:
        await websocket.close(code=1008, reason="Simulation session not found")
        return

    stream = session.stream
    subscriber_id, subscriber_queue, current_tick = session_manager.subscribe(
        simulation_id
    )

    try:
        await websocket.send_json(stream.ready_message(simulation_id, current_tick))
        await websocket.send_json(stream.metadata_message(simulation_id))

        async with anyio.create_task_group() as tasks:
            send_lock = anyio.Lock()

            async def receive_controls() -> None:
                try:
                    while True:
                        control = await websocket.receive_json()
                        async with send_lock:
                            targets_session = await _control_targets_session(
                                websocket, simulation_id, control
                            )
                        if not targets_session:
                            continue

                        message_type = _message_type(control)
                        if not session_manager.apply_control(
                            simulation_id, message_type
                        ):
                            status = session_manager.get(simulation_id).status
                            async with send_lock:
                                await _send_control_error(
                                    websocket,
                                    simulation_id,
                                    control,
                                    f"while simulation status is {status}",
                                )
                except WebSocketDisconnect:
                    pass
                finally:
                    tasks.cancel_scope.cancel()

            async def forward_broadcasts() -> None:
                try:
                    while True:
                        event = await subscriber_queue.get()
                        if event.payload is not None:
                            async with send_lock:
                                await websocket.send_text(event.payload)
                            # Some ASGI transports complete send_text immediately.
                            # Yield explicitly so a full subscriber queue cannot
                            # starve PAUSE/STOP controls until playback completes.
                            await anyio.sleep(0)
                        if event.terminal:
                            return
                finally:
                    tasks.cancel_scope.cancel()

            tasks.start_soon(receive_controls)
            tasks.start_soon(forward_broadcasts)
    finally:
        session_manager.unsubscribe(simulation_id, subscriber_id)
