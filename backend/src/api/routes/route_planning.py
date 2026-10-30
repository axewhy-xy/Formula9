"""Independent route-preview endpoint."""

from fastapi import APIRouter, HTTPException

from src.routing.road_graph import RoadGraphError
from src.schemas.route import CreateRouteRequest, RouteResponse
from src.services.route_planner import (
    RouteNotFoundError,
    RoutePlanningError,
    SceneRevisionMismatchError,
    get_route_planner,
)
from src.services.scene_repository import SceneNotFoundError, SceneRepositoryError


router = APIRouter(prefix="/routes", tags=["routes"])


@router.post("", response_model=RouteResponse)
def create_route(payload: CreateRouteRequest) -> RouteResponse:
    """Compute a preview without creating or mutating a simulation session."""

    try:
        return get_route_planner().plan(payload)
    except SceneNotFoundError as error:
        raise HTTPException(status_code=404, detail=str(error)) from error
    except SceneRevisionMismatchError as error:
        raise HTTPException(status_code=409, detail=str(error)) from error
    except RouteNotFoundError as error:
        raise HTTPException(status_code=422, detail=str(error)) from error
    except (RoadGraphError, SceneRepositoryError) as error:
        raise HTTPException(status_code=503, detail=str(error)) from error
    except RoutePlanningError as error:
        raise HTTPException(status_code=422, detail=str(error)) from error
