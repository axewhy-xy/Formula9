"""
In-memory ownership of simulation lifecycle and trajectory playback state.

Author: Lau Xiang Yang, CC
Date Created: 12/09/2026
Last Modified: 20/09/2026

Description:
    This module provides a SessionManager class that manages simulation sessions, 
    including their metadata and trajectory playback state. It allows for creating, 
    retrieving, subscribing to, and controlling simulation sessions. 
    
    The SessionManager ensures thread-safe access to session data and handles the 
    playback of trajectory data to subscribed clients.

"""

from __future__ import annotations

import asyncio
import json
from dataclasses import dataclass, field, replace
from datetime import UTC, datetime
from threading import RLock
from typing import Callable
from uuid import uuid4

from contracts.common import GeographicPoint
from src.core.errors import InvalidSimulationSourceError, SessionNotFoundError
from src.schemas.route import RouteResponse
from src.schemas.simulation import CreateSimulationRequest, SimulationResponse
from src.services.route_planner import RoutePlanNotFoundError, get_planned_route
from src.services.trajectory_stream import (
    TrajectorySourceError,
    TrajectoryStream,
    build_planned_route_stream,
    build_trajectory_stream,
)
from src.services.scene_repository import SceneRepositoryError, get_scene_repository
from src.schemas.scene import SceneBounds, SCENE_TRANSFORM_REVISION
from src.routing.map_matcher import (
    MAXIMUM_MATCH_DISTANCE_METRES,
    match_trajectory_to_roads,
)


StreamFactory = Callable[..., TrajectoryStream]
RouteResolver = Callable[[str], RouteResponse]
GEOGRAPHIC_ALIGNMENT_TOLERANCE_METRES = 100.0


@dataclass(slots=True)
class SimulationSession:
    response: SimulationResponse
    stream: TrajectoryStream
    current_tick: int = 0
    subscribers: dict[int, asyncio.Queue[PlaybackEvent]] = field(default_factory=dict)
    next_subscriber_id: int = 0
    playback_task: asyncio.Task[None] | None = None
    state_changed: asyncio.Event | None = None


@dataclass(frozen=True, slots=True)
class PlaybackEvent:
    """An immutable wire message delivered to one or more subscribers."""

    payload: str | None
    terminal: bool = False


def _as_dict(value: object) -> dict:
    to_dict = getattr(value, "to_dict")
    return to_dict()


class SessionManager:
    """Stores public session metadata together with its authoritative stream."""

    def __init__(
        self,
        stream_factory: StreamFactory = build_trajectory_stream,
        route_resolver: RouteResolver = get_planned_route,
    ) -> None:
        self._sessions: dict[str, SimulationSession] = {}
        self._stream_factory = stream_factory
        self._route_resolver = route_resolver
        self._lock = RLock()

    def create(self, request: CreateSimulationRequest) -> SimulationResponse:
        coordinate_origin = None
        scene = None
        scene_repository = None
        if request.scene_id is not None:
            try:
                scene_repository = get_scene_repository()
                scene = scene_repository.metadata(request.scene_id)
            except SceneRepositoryError as error:
                raise InvalidSimulationSourceError(str(error)) from error
            coordinate_origin = GeographicPoint(
                latitude=scene.coordinates.origin.latitude,
                longitude=scene.coordinates.origin.longitude,
            )
        try:
            if request.route_id is not None:
                if scene is None or coordinate_origin is None:
                    raise TrajectorySourceError(
                        "A planned-route simulation requires a static scene"
                    )
                route = self._route_resolver(request.route_id)
                if route.scene_id != request.scene_id:
                    raise TrajectorySourceError(
                        "The planned route belongs to a different scene"
                    )
                if route.scene_revision != scene.scene_revision:
                    raise TrajectorySourceError(
                        "The planned route is stale; plan it again against the current scene"
                    )
                if route.transform_revision != scene.transform_revision:
                    raise TrajectorySourceError(
                        "The planned route uses a different scene transform"
                    )
                stream = build_planned_route_stream(
                    route=route,
                    playback_speed=request.playback_speed,
                    coordinate_origin=coordinate_origin,
                )
            else:
                stream = self._stream_factory(
                    dataset_name=request.dataset_name,
                    sequence_id=request.sequence_id,
                    playback_speed=request.playback_speed,
                    coordinate_origin=coordinate_origin,
                )
        except (RoutePlanNotFoundError, TrajectorySourceError) as error:
            raise InvalidSimulationSourceError(str(error)) from error

        if request.alignment_mode == "GEOGRAPHIC" and scene is not None:
            self._validate_geographic_alignment(
                stream,
                scene.bounds,
                scene_id=request.scene_id,
                sequence_id=request.sequence_id,
            )

        scene_revision = None if scene is None else scene.scene_revision
        transform_revision = (
            SCENE_TRANSFORM_REVISION
            if scene is not None
            else "kitti-mercator-enu-v1"
            if request.ego_source == "KITTI_OXTS"
            else "synthetic-local-v1"
        )
        stream = replace(
            stream,
            metadata=replace(
                stream.metadata,
                ego_source=request.ego_source,
                alignment_mode=request.alignment_mode,
                scene_id=request.scene_id,
                scene_revision=scene_revision,
                transform_revision=transform_revision,
            ),
        )

        if (
            request.ego_source == "KITTI_OXTS"
            and request.alignment_mode == "GEOGRAPHIC"
            and scene_repository is not None
        ):
            route_bounds = self._trajectory_bounds(stream)
            padding = MAXIMUM_MATCH_DISTANCE_METRES
            try:
                road_chunk = scene_repository.chunk(
                    request.scene_id,
                    SceneBounds(
                        min_x=route_bounds["min_x"] - padding,
                        min_y=route_bounds["min_y"] - padding,
                        max_x=route_bounds["max_x"] + padding,
                        max_y=route_bounds["max_y"] + padding,
                    ),
                    lod=3,
                    include_buildings=False,
                    include_pois=False,
                )
            except SceneRepositoryError as error:
                raise InvalidSimulationSourceError(str(error)) from error
            matching = match_trajectory_to_roads(stream.samples, road_chunk.roads)
            stream = replace(
                stream,
                road_matches=matching.matches,
                metadata=replace(stream.metadata, map_matching=matching.summary),
            )

        simulation_id = str(uuid4())
        response = SimulationResponse(
            simulation_id=simulation_id,
            scenario_name=request.scenario_name,
            status="READY",
            created_at=datetime.now(UTC),
            websocket_url=f"/ws/simulations/{simulation_id}",
            dataset_name=stream.metadata.dataset_name,
            sequence_id=stream.metadata.sequence_id,
            route_id=request.route_id,
            playback_speed=request.playback_speed,
            scene_id=request.scene_id,
            scene_revision=scene_revision,
            transform_revision=transform_revision,
            ego_source=request.ego_source,
            alignment_mode=request.alignment_mode,
            coordinates=_as_dict(stream.coordinates),
            route_summary={
                "distance": round(stream.route_distance, 3),
                "distance_unit": "m",
                "estimated_duration": round(
                    stream.duration / request.playback_speed, 6
                ),
                "duration_unit": "s",
                "route_point_count": len(stream.samples),
                "bounds": self._trajectory_bounds(stream),
            },
            trajectory=_as_dict(stream.metadata),
        )
        with self._lock:
            self._sessions[simulation_id] = SimulationSession(response, stream)
        return response

    @staticmethod
    def _trajectory_bounds(stream: TrajectoryStream) -> dict[str, float]:
        """Return the complete route extent for bounded static-scene loading."""

        x_values = [sample.position.x for sample in stream.samples]
        y_values = [sample.position.y for sample in stream.samples]
        return {
            "min_x": min(x_values),
            "min_y": min(y_values),
            "max_x": max(x_values),
            "max_y": max(y_values),
        }

    @staticmethod
    def _validate_geographic_alignment(
        stream: TrajectoryStream,
        bounds: SceneBounds,
        *,
        scene_id: str,
        sequence_id: str | None,
    ) -> None:
        """Reject a geographic replay whose samples never approach the scene."""

        tolerance = GEOGRAPHIC_ALIGNMENT_TOLERANCE_METRES
        for sample in stream.samples:
            x = sample.position.x
            y = sample.position.y
            if (
                bounds.min_x - tolerance <= x <= bounds.max_x + tolerance
                and bounds.min_y - tolerance <= y <= bounds.max_y + tolerance
            ):
                return
        raise InvalidSimulationSourceError(
            "GEOGRAPHIC alignment rejected: KITTI sequence "
            f"'{sequence_id}' does not intersect scene '{scene_id}' within "
            f"{tolerance:.0f} metres"
        )

    def get(self, session_id: str) -> SimulationResponse:
        return self.get_session(session_id).response

    def get_session(self, session_id: str) -> SimulationSession:
        with self._lock:
            session = self._sessions.get(session_id)
            if session is None:
                raise SessionNotFoundError(
                    f"Simulation session '{session_id}' was not found."
                )
            return session

    def subscribe(
        self, session_id: str
    ) -> tuple[int, asyncio.Queue[PlaybackEvent], int]:
        """Attach a client queue and ensure this session has one playback task."""

        with self._lock:
            session = self.get_session(session_id)
            subscriber_id = session.next_subscriber_id
            session.next_subscriber_id += 1
            # A slow presentation client must not grow memory without bound.
            # The authoritative session history remains in the stream, so live
            # delivery may discard the oldest unrendered snapshot when full.
            queue: asyncio.Queue[PlaybackEvent] = asyncio.Queue(maxsize=32)
            session.subscribers[subscriber_id] = queue

            if session.response.status == "COMPLETED":
                payload = session.stream.complete_message(
                    session_id, session.stream.tick_count - 1
                )
                queue.put_nowait(PlaybackEvent(json.dumps(payload), terminal=True))
            elif session.response.status == "STOPPED":
                queue.put_nowait(PlaybackEvent(None, terminal=True))
            else:
                self._ensure_playback_task(session_id, session)

            return subscriber_id, queue, session.current_tick

    def unsubscribe(self, session_id: str, subscriber_id: int) -> None:
        with self._lock:
            session = self._sessions.get(session_id)
            if session is not None:
                session.subscribers.pop(subscriber_id, None)

    def apply_control(self, session_id: str, message_type: str) -> bool:
        """Apply a playback command to the shared session state.

        Returns ``False`` when the command is known but invalid for the current
        state. Unknown commands are also rejected by this boundary.
        """

        with self._lock:
            session = self.get_session(session_id)
            status = session.response.status
            accepted = False
            next_status = status

            if message_type in {"START", "RESUME"}:
                if status in {"READY", "PAUSED", "RUNNING"}:
                    accepted = True
                    next_status = "RUNNING"
            elif message_type == "PAUSE":
                if status == "RUNNING":
                    accepted = True
                    next_status = "PAUSED"
            elif message_type == "STOP":
                if status in {"READY", "PAUSED", "RUNNING"}:
                    accepted = True
                    next_status = "STOPPED"

            if not accepted:
                return False

            self._replace_status(session, next_status)
            self._ensure_playback_task(session_id, session)
            if session.state_changed is not None:
                session.state_changed.set()
            return True

    def _replace_status(self, session: SimulationSession, status: str) -> None:
        copy_model = getattr(session.response, "model_copy", session.response.copy)
        session.response = copy_model(update={"status": status})

    def _ensure_playback_task(
        self, session_id: str, session: SimulationSession
    ) -> None:
        if session.response.status in {"COMPLETED", "STOPPED"}:
            return
        if session.playback_task is not None and not session.playback_task.done():
            return
        session.state_changed = asyncio.Event()
        session.playback_task = asyncio.create_task(
            self._playback_loop(session_id),
            name=f"simulation-playback-{session_id}",
        )

    def _broadcast(self, session: SimulationSession, event: PlaybackEvent) -> None:
        for queue in tuple(session.subscribers.values()):
            if queue.full():
                try:
                    queue.get_nowait()
                except asyncio.QueueEmpty:
                    pass
            queue.put_nowait(event)

    async def _playback_loop(self, session_id: str) -> None:
        """Advance one session clock and fan each message out to all clients."""

        try:
            while True:
                with self._lock:
                    session = self._sessions.get(session_id)
                    if session is None:
                        return
                    status = session.response.status
                    state_changed = session.state_changed

                    if status == "STOPPED":
                        self._broadcast(session, PlaybackEvent(None, terminal=True))
                        return

                    if status == "RUNNING":
                        tick = session.current_tick
                        if tick >= session.stream.tick_count:
                            self._replace_status(session, "COMPLETED")
                            complete = session.stream.complete_message(
                                session_id, session.stream.tick_count - 1
                            )
                            self._broadcast(
                                session,
                                PlaybackEvent(json.dumps(complete), terminal=True),
                            )
                            return

                        # Serialize once. The resulting string is immutable and the
                        # same PlaybackEvent is queued for every connected client.
                        tick_payload = json.dumps(
                            session.stream.tick_message(session_id, tick)
                        )
                        session.current_tick = tick + 1
                        self._broadcast(session, PlaybackEvent(tick_payload))

                        if session.current_tick >= session.stream.tick_count:
                            self._replace_status(session, "COMPLETED")
                            complete = session.stream.complete_message(session_id, tick)
                            self._broadcast(
                                session,
                                PlaybackEvent(json.dumps(complete), terminal=True),
                            )
                            return

                        interval = session.stream.tick_interval
                    else:
                        interval = None

                if state_changed is None:
                    return

                if interval is None:
                    await state_changed.wait()
                    state_changed.clear()
                    continue

                try:
                    await asyncio.wait_for(state_changed.wait(), timeout=interval)
                    state_changed.clear()
                except TimeoutError:
                    pass
        except asyncio.CancelledError:
            raise

    def delete(self, session_id: str) -> None:
        with self._lock:
            session = self.get_session(session_id)
            self._broadcast(session, PlaybackEvent(None, terminal=True))
            del self._sessions[session_id]
            self._cancel_task(session.playback_task)

    def clear(self) -> None:
        """Remove all sessions; intended for isolated tests."""
        with self._lock:
            tasks = [session.playback_task for session in self._sessions.values()]
            self._sessions.clear()
        for task in tasks:
            self._cancel_task(task)

    @staticmethod
    def _cancel_task(task: asyncio.Task[None] | None) -> None:
        if task is None or task.done():
            return
        loop = task.get_loop()
        if loop.is_running():
            loop.call_soon_threadsafe(task.cancel)
        else:
            task.cancel()


session_manager = SessionManager()


__all__ = [
    "PlaybackEvent",
    "SessionManager",
    "SimulationSession",
    "session_manager",
]
