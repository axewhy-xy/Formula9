"""Durable v2 command reconciliation and event replay endpoints."""

from __future__ import annotations

import asyncio
from threading import Lock

from typing import Annotated

from fastapi import APIRouter, Depends, FastAPI, Header, Request, Response
from fastapi.responses import JSONResponse
from starlette.authentication import UnauthenticatedUser

from contracts.v2.models import DispatcherCommand, Id, TrainingSession
from contracts.v2.sessions import CreateSessionRequest, SessionListResponse
from contracts.v2.route_edit import RouteApprovalRequest, RouteEditPreview, RouteEditRequest, RouteEditorContext
from src.core.config import get_settings
from src.core.training_telemetry import boundary
from src.persistence.training_store import TrainingStore, TrainingStoreError
from src.services.battery_decision import submit_battery_decision
from src.services.incidents import IncidentService, get_incident_service
from src.services.route_edit import RouteEditService, get_route_edit_service
from src.services.training_sessions import TrainingSessionService
from src.services.integrated_actor import SCENARIO_ID, services_for

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


async def initialize_training_service(application: FastAPI, store: TrainingStore) -> TrainingSessionService:
    services = getattr(application.state, "training_services", None)
    if services is None:
        services = application.state.training_services = {}
    if store not in services:
        services[store] = TrainingSessionService(store)
    service = services[store]
    await service.initialize()
    return service


async def get_training_service(request: Request, store: TrainingStore = Depends(get_training_store)) -> TrainingSessionService:
    return await initialize_training_service(request.app, store)


@router.get("", response_model=SessionListResponse)
async def list_sessions(actor_id: str = Depends(get_actor_id),
                        service: TrainingSessionService = Depends(get_training_service)) -> dict:
    sessions = await asyncio.to_thread(service.store.list_sessions)
    return {"sessions": sessions, "live_count": sum(s["occupies_capacity"] for s in sessions),
            "capacity": service.MAX_SESSIONS}


@router.post("", response_model=TrainingSession, status_code=201)
async def create_session(payload: CreateSessionRequest, idempotency_key: Annotated[Id, Header()],
                         actor_id: str = Depends(get_actor_id),
                         service: TrainingSessionService = Depends(get_training_service)) -> TrainingSession:
    return (await service.create(payload, actor_id, idempotency_key)).session


@router.post("/compact")
async def compact_sessions(actor_id: str = Depends(get_actor_id),
                           service: TrainingSessionService = Depends(get_training_service)) -> dict:
    return await service.compact()


@router.delete("/{session_id}", status_code=204)
async def delete_session(session_id: str, actor_id: str = Depends(get_actor_id),
                          service: TrainingSessionService = Depends(get_training_service)) -> Response:
    await service.delete(session_id)
    return Response(status_code=204)


@router.get("/{session_id}/snapshot")
async def snapshot(session_id: str, actor_id: str = Depends(get_actor_id),
                   service: TrainingSessionService = Depends(get_training_service)) -> dict:
    return (await service.snapshot(session_id)).model_dump(mode="json")


@router.get("/{session_id}/routes/{route_id}/editor", response_model=RouteEditorContext)
def route_editor(session_id: str, route_id: str,
                 actor_id: str = Depends(get_actor_id),
                 store: TrainingStore = Depends(get_training_store),
                 service: RouteEditService = Depends(get_route_edit_service)) -> RouteEditorContext:
    baseline = store.snapshot(session_id)
    service, _ = services_for(baseline, routes=service)
    return service.editor_context(baseline, route_id)


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
        result = service.preview(snapshot, draft)
        fields.update(preview_id=result.preview_id, outcome=result.validation)
        return result


@router.post("/{session_id}/route-approvals")
async def approve_route(session_id: str, approval: RouteApprovalRequest,
                  actor_id: str = Depends(get_actor_id),
                  store: TrainingStore = Depends(get_training_store),
                  service: RouteEditService = Depends(get_route_edit_service),
                  runtime: TrainingSessionService = Depends(get_training_service)) -> dict:
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
                   service: TrainingSessionService = Depends(get_training_service)) -> dict:
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
    return (await asyncio.to_thread(store.accept, actor_id, command)).model_dump(mode="json")


@router.post("/{session_id}/incident-previews")
def incident_preview(session_id: str, command: DispatcherCommand,
                     actor_id: str = Depends(get_actor_id),
                     store: TrainingStore = Depends(get_training_store),
                     incidents: IncidentService = Depends(get_incident_service)) -> dict:
    _, incidents = services_for(store.snapshot(session_id), incidents=incidents)
    return incidents.preview(store, session_id, command)


@router.get("/{session_id}/incidents/{incident_id}/history")
def incident_history(session_id: str, incident_id: str,
                     actor_id: str = Depends(get_actor_id),
                     store: TrainingStore = Depends(get_training_store),
                     incidents: IncidentService = Depends(get_incident_service)) -> dict:
    _, incidents = services_for(store.snapshot(session_id), incidents=incidents)
    return incidents.history(store, session_id, incident_id)


@router.get("/{session_id}/incidents/{incident_id}/debrief")
def incident_debrief(session_id: str, incident_id: str,
                     actor_id: str = Depends(get_actor_id),
                     store: TrainingStore = Depends(get_training_store),
                     incidents: IncidentService = Depends(get_incident_service)) -> dict:
    _, incidents = services_for(store.snapshot(session_id), incidents=incidents)
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
