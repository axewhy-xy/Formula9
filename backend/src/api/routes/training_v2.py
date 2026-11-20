"""Durable v2 command reconciliation and event replay endpoints."""

from __future__ import annotations

import asyncio
import inspect
import json
import logging
from threading import Lock

from typing import Annotated

from fastapi import APIRouter, Depends, FastAPI, Header, Query, Request, Response
from fastapi.responses import JSONResponse
from starlette.authentication import UnauthenticatedUser

from contracts.v2.models import ChargeSchedulePreview, ChargeScheduleRequest, DispatcherCommand, Id, TrainingSession
from contracts.v2.sessions import CreateSessionRequest, SessionListResponse
from contracts.v2.route_edit import RouteApprovalRequest, RouteEditPreview, RouteEditRequest, RouteEditorContext, RouteOutcome
from src.core.config import get_settings
from src.core.training_telemetry import boundary
from src.persistence.training_store import TrainingStore, TrainingStoreError
from src.services.battery_decision import submit_battery_decision
from src.services.charge_booking import BOOKING_COMMANDS, preview_slot
from src.services.incidents import IncidentService, get_incident_service
from src.services.route_edit import RouteEditService, get_route_edit_service
from src.services.route_outcome import route_outcome
from src.services.session_gateway import WorkerStartError, create_session_service
from src.services.training_sessions import SessionServiceBase, TrainingSessionService
from src.services.integrated_actor import SCENARIO_ID, get_workflow_definition, services_for

logger = logging.getLogger(__name__)
_store: TrainingStore | None = None
_store_lock = Lock()


def get_training_store() -> TrainingStore:
    global _store
    with _store_lock:
        if _store is None:
            _store = TrainingStore(get_settings().training_database_path)
        return _store


async def bind_training_trace(request: Request, store: TrainingStore = Depends(get_training_store)) -> None:
    request.scope["training_trace"] = store.trace


router = APIRouter(prefix="/v2/sessions", tags=["training-v2"], dependencies=[Depends(bind_training_trace)])


def close_training_store() -> None:
    global _store
    with _store_lock:
        if _store is not None:
            _store.close()
            _store = None


def get_actor_id(request: Request) -> str:
    """Use a trusted authentication middleware principal, never a body/header ID."""
    user = request.scope.get("user", UnauthenticatedUser())
    if not getattr(user, "is_authenticated", False):
        raise TrainingStoreError("ROLE_DENIED", "Training API requires an authenticated actor")
    actor_id = getattr(user, "username", None)
    if not actor_id:
        raise TrainingStoreError("ROLE_DENIED", "Authenticated actor has no stable identity")
    return str(actor_id)


async def initialize_training_service(application: FastAPI, store: TrainingStore) -> SessionServiceBase:
    """The application's session service for store: sessions in worker processes (F9_SESSION_WORKERS,
    session_gateway) or in this one. If the workers cannot start, sessions run in this process.

    Requests may wait on the gateway's start together. The gateway undoes a failed start before raising
    and fails every later start at once, and only the first request to see the failure installs the
    in-process service (before closing the gateway, so none in between finds the closed one); the
    others use it. So no session is ever ticked by both, nor by two in-process services."""
    services = getattr(application.state, "training_services", None)
    if services is None:
        services = application.state.training_services = {}
    retention = get_settings().training_retention_keep_latest
    if store not in services:
        services[store] = create_session_service(store, retention_keep_latest=retention)
    service = services[store]
    try:
        await service.initialize()
    except WorkerStartError:
        if services.get(store) is service:
            logger.exception("Session workers could not start; running training sessions in this process")
            services[store] = TrainingSessionService(store, retention_keep_latest=retention)
            await service.close()
        service = services[store]
        await service.initialize()
    return service


async def get_training_service(request: Request, store: TrainingStore = Depends(get_training_store)) -> SessionServiceBase:
    return await initialize_training_service(request.app, store)


@router.get("", response_model=SessionListResponse)
async def list_sessions(limit: Annotated[int, Query(ge=1, le=500)] = 100, offset: Annotated[int, Query(ge=0)] = 0,
                        actor_id: str = Depends(get_actor_id),
                        service: SessionServiceBase = Depends(get_training_service)) -> dict:
    page = await asyncio.to_thread(service.store.session_page, limit, offset)
    return {**page, "capacity": service.MAX_SESSIONS}


@router.post("", response_model=TrainingSession, status_code=201)
async def create_session(payload: CreateSessionRequest, idempotency_key: Annotated[Id, Header()],
                         actor_id: str = Depends(get_actor_id),
                         service: SessionServiceBase = Depends(get_training_service)) -> TrainingSession:
    return (await service.create(payload, actor_id, idempotency_key)).session


@router.post("/purge")
async def purge_sessions(keep_latest: Annotated[int, Query(ge=0)], actor_id: str = Depends(get_actor_id),
                         service: SessionServiceBase = Depends(get_training_service)) -> dict:
    return await service.purge(keep_latest)


@router.post("/compact")
async def compact_sessions(actor_id: str = Depends(get_actor_id),
                           service: SessionServiceBase = Depends(get_training_service)) -> dict:
    return await service.compact()


@router.delete("/{session_id}", status_code=204)
async def delete_session(session_id: str, actor_id: str = Depends(get_actor_id),
                          service: SessionServiceBase = Depends(get_training_service)) -> Response:
    await service.delete(session_id)
    return Response(status_code=204)


@router.get("/{session_id}/snapshot")
async def snapshot(session_id: str, actor_id: str = Depends(get_actor_id),
                   service: SessionServiceBase = Depends(get_training_service)) -> Response:
    # The committed JSON as it is: a live session's is kept in memory at each commit (TrainingStore),
    # so a poll neither reads SQLite nor parses and re-encodes the snapshot
    return Response(content=await service.snapshot_json(session_id), media_type="application/json")


@router.get("/{session_id}/traffic")
async def traffic(session_id: str, after_tick: Annotated[int, Query(ge=-1)] = -1,
                  wait_ms: Annotated[int, Query(ge=0, le=5000)] = 0,
                  actor_id: str = Depends(get_actor_id),
                  service: SessionServiceBase = Depends(get_training_service)) -> Response:
    """The background traffic and the shuttles' bodies as the backend drives them, for a 3D view to draw.

    Frames after after_tick, oldest first, one a traffic step (ten a second) while the session runs, at
    most the newest second's worth. With wait_ms and none yet, the reply waits up to that long for the
    next, so a view that asks again at once hears of each frame as it is made. Each frame has its tick and
    simulation time; vehicle_count vehicles on the roads, base64 of 14 bytes each, little-endian (uint16
    index in /api/traffic/{area}'s vehicles, int32 x cm, int32 y cm, int16 heading in tenths of a degree
    anticlockwise from east, uint16 speed cm/s); each shuttle's body: its middle, heading and speed; and
    signal_faults, each junction a signal fault has stuck on red ({junction_id, mode, until_s}). A world
    event's pinned vehicles are among the vehicles, standing still. With pedestrians (F9_PEDESTRIANS),
    pedestrian_count walkers, base64 of 14 bytes each, little-endian (uint16 index in the walkways file's
    seeds, int32 x cm, int32 y cm, int16 heading in tenths of a degree, uint8 speed in 2 cm/s, uint8 flags:
    1 waiting at a kerb, 2 on a crossing's road part, 4 on a kerb ramp, 8 standing a while); without, the
    two are left out. Presentation only; the snapshot stays the authority on where a shuttle is along its
    route.
    """
    frames = await service.wait_for_traffic(session_id, after_tick, wait_ms / 1000)
    return Response(content='{"session_id":%s,"frames":[%s]}' % (json.dumps(session_id), ",".join(frames)),
                    media_type="application/json")


@router.get("/{session_id}/signals/state")
async def signal_state(session_id: str, actor_id: str = Depends(get_actor_id),
                       service: SessionServiceBase = Depends(get_training_service)) -> dict:
    """Every signalled junction's colours now, on the session's simulation clock, as
    /api/road-features/{area}/signals/state gives them for any time, but with this session's signal
    faults: a faulted junction shows red on every phase and carries {"mode", "until_s"} under "fault"."""
    state = service.signal_state(session_id)
    # A session in a worker process answers from there
    return await state if inspect.isawaitable(state) else state


@router.get("/{session_id}/routes/{route_id}/editor", response_model=RouteEditorContext)
def route_editor(session_id: str, route_id: str,
                 actor_id: str = Depends(get_actor_id),
                 store: TrainingStore = Depends(get_training_store),
                 service: RouteEditService = Depends(get_route_edit_service)) -> RouteEditorContext:
    baseline = store.snapshot(session_id)
    service, _ = services_for(baseline, routes=service)
    return service.for_route(baseline, route_id).editor_context(baseline, route_id)


@router.get("/{session_id}/routes/{route_id}/outcome", response_model=RouteOutcome)
def route_outcome_report(session_id: str, route_id: str,
                         actor_id: str = Depends(get_actor_id),
                         store: TrainingStore = Depends(get_training_store)) -> RouteOutcome:
    return route_outcome(store, session_id, route_id)


@router.post("/{session_id}/route-previews", response_model=RouteEditPreview)
def route_preview(session_id: str, draft: RouteEditRequest,
                  actor_id: str = Depends(get_actor_id),
                  store: TrainingStore = Depends(get_training_store),
                  service: RouteEditService = Depends(get_route_edit_service)) -> RouteEditPreview:
    with store.trace.span("route.validation", session_id=session_id, route_id=draft.route_id,
                          draft_revision=draft.draft_revision) as fields:
        snapshot = store.snapshot(session_id)
        service, _ = services_for(snapshot, routes=service)
        fields.update(boundary(snapshot))
        result = service.for_route(snapshot, draft.route_id).draft_preview(snapshot, draft)
        fields.update(preview_id=result.preview_id, outcome=result.validation)
        return result


@router.post("/{session_id}/route-approvals")
async def approve_route(session_id: str, approval: RouteApprovalRequest,
                  actor_id: str = Depends(get_actor_id),
                  store: TrainingStore = Depends(get_training_store),
                  service: RouteEditService = Depends(get_route_edit_service),
                  runtime: SessionServiceBase = Depends(get_training_service)) -> dict:
    if runtime.owns(session_id):
        return (await runtime.approve_route(actor_id, session_id, approval)).model_dump(mode="json")
    baseline = await asyncio.to_thread(store.snapshot, session_id)
    service, _ = services_for(baseline, routes=service)
    return (await asyncio.to_thread(service.approve, store, actor_id, session_id, approval)).model_dump(mode="json")


@router.post("/{session_id}/commands", status_code=202)
async def submit_command(session_id: str, command: DispatcherCommand,
                   actor_id: str = Depends(get_actor_id),
                   store: TrainingStore = Depends(get_training_store),
                   incidents: IncidentService = Depends(get_incident_service),
                   service: SessionServiceBase = Depends(get_training_service)) -> dict:
    if command.session_id != session_id:
        raise TrainingStoreError("INVALID_REFERENCE", "Command session does not match URL")
    if service.owns(session_id) and command.command_type != "APPROVE_ROUTE":
        baseline = await asyncio.to_thread(store.snapshot, session_id)
        if baseline.scenario.scenario_id == SCENARIO_ID:
            return (await service.submit(actor_id, command)).model_dump(mode="json")
    if service.owns(session_id) and command.command_type in {"START", "PAUSE", "RESUME", "RESET", "ARCHIVE"}:
        return (await service.submit(actor_id, command)).model_dump(mode="json")
    if command.command_type == "ARCHIVE":
        # Terminal attempts still replay their receipt through accept(). The
        # store must not queue an archive for an unrelated fixture to execute.
        baseline = await asyncio.to_thread(store.snapshot, session_id)
        if baseline.session.status not in {"ARCHIVED", "FAILED"}:
            raise TrainingStoreError("INVALID_TRANSITION", "Only live fleet attempts support archive")
        return (await asyncio.to_thread(store.accept, actor_id, command)).model_dump(mode="json")
    if command.command_type == "APPROVE_ROUTE":
        raise TrainingStoreError("INVALID_PAYLOAD", "Use the route approval endpoint with a validated preview")
    if command.command_type == "APPROVE_INCIDENT_OPTION" and incidents.owns(await asyncio.to_thread(store.snapshot, session_id)):
        return (await asyncio.to_thread(incidents.decide, store, actor_id, command)).model_dump(mode="json")
    if command.command_type in {"RESERVE_CHARGER", "CONTINUE_SERVICE"}:
        return (await asyncio.to_thread(submit_battery_decision, store, actor_id, command)).model_dump(mode="json")
    if command.command_type in BOOKING_COMMANDS:
        # Only the integrated scenario's actor starts a slot; one booked anywhere else would never run
        raise TrainingStoreError("INVALID_TRANSITION", "This scenario does not run charging slots")
    return (await asyncio.to_thread(store.accept, actor_id, command)).model_dump(mode="json")


@router.post("/{session_id}/charge-schedule-previews", response_model=ChargeSchedulePreview)
async def charge_schedule_preview(session_id: str, request: ChargeScheduleRequest,
                                  actor_id: str = Depends(get_actor_id),
                                  service: SessionServiceBase = Depends(get_training_service)) -> ChargeSchedulePreview:
    snapshot = await service.snapshot(session_id)
    if snapshot.scenario.scenario_id != SCENARIO_ID:
        raise TrainingStoreError("INVALID_TRANSITION", "This scenario does not run charging slots")
    data = get_workflow_definition().data
    return preview_slot(snapshot, request, data["charge_s"], data.get("charge_time_scale"))


@router.post("/{session_id}/incident-previews")
def incident_preview(session_id: str, command: DispatcherCommand,
                     actor_id: str = Depends(get_actor_id),
                     store: TrainingStore = Depends(get_training_store),
                     incidents: IncidentService = Depends(get_incident_service)) -> dict:
    snapshot = store.snapshot(session_id)
    _, incidents = services_for(snapshot, incidents=incidents)
    incidents = incidents.for_incident(store, snapshot, command.target_id)
    return incidents.preview(store, session_id, command)


@router.get("/{session_id}/incidents/{incident_id}/history")
def incident_history(session_id: str, incident_id: str,
                     actor_id: str = Depends(get_actor_id),
                     store: TrainingStore = Depends(get_training_store),
                     incidents: IncidentService = Depends(get_incident_service)) -> dict:
    snapshot = store.snapshot(session_id)
    _, incidents = services_for(snapshot, incidents=incidents)
    incidents = incidents.for_incident(store, snapshot, incident_id)
    return incidents.history(store, session_id, incident_id)


@router.get("/{session_id}/incidents/{incident_id}/debrief")
def incident_debrief(session_id: str, incident_id: str,
                     actor_id: str = Depends(get_actor_id),
                     store: TrainingStore = Depends(get_training_store),
                     incidents: IncidentService = Depends(get_incident_service)) -> dict:
    snapshot = store.snapshot(session_id)
    _, incidents = services_for(snapshot, incidents=incidents)
    incidents = incidents.for_incident(store, snapshot, incident_id)
    return incidents.debrief(store, session_id, incident_id)


@router.get("/{session_id}/commands/{key}")
def command_status(session_id: str, key: str,
                   actor_id: str = Depends(get_actor_id),
                   store: TrainingStore = Depends(get_training_store)) -> dict:
    return store.command_status(session_id, actor_id, key).model_dump(mode="json")


@router.get("/{session_id}/events")
def replay_events(session_id: str, after: int = 0, limit: int = 100,
                  actor_id: str = Depends(get_actor_id),
                  store: TrainingStore = Depends(get_training_store)) -> dict:
    return store.replay(session_id, after, limit).model_dump(mode="json")


async def training_store_error_handler(_: Request, error: TrainingStoreError) -> JSONResponse:
    code = error.code
    status = 404 if code == "INVALID_REFERENCE" else 403 if code == "ROLE_DENIED" else 503 if code in {"QUEUE_FULL", "SESSION_OWNED"} else 409 if code in {"STALE_REVISION", "IDEMPOTENCY_CONFLICT", "SESSION_RECOVERING"} else 400
    return JSONResponse(status_code=status, content={"error": {
        "code": code, "message": str(error)[:512],
        "retryable": code in {"STALE_REVISION", "QUEUE_FULL", "SESSION_RECOVERING"},
        "current_revision": error.current_revision,
    }})
