"""Pydantic models for independent route preview requests."""

from typing import Literal

from pydantic import BaseModel, Field, FiniteFloat

from src.schemas.scene import SceneBounds, ScenePoint, SCENE_TRANSFORM_REVISION


class RoutePoint(BaseModel):
    """A point in the scene's canonical x-east/y-north metre frame."""

    x: FiniteFloat
    y: FiniteFloat


class CreateRouteRequest(BaseModel):
    contract_version: Literal["1.0"] = "1.0"
    scene_id: str = Field(default="default", min_length=1, max_length=100)
    scene_revision: str = Field(min_length=1, max_length=200)
    start: RoutePoint
    destination: RoutePoint
    cost: Literal["distance"] = "distance"


class SnappedRoutePoint(ScenePoint):
    node_id: int
    snapping_distance: float = Field(ge=0.0)


class RouteResponse(BaseModel):
    route_id: str
    scene_id: str
    scene_revision: str
    transform_revision: str = SCENE_TRANSFORM_REVISION
    contract_version: Literal["1.0"] = "1.0"
    cost: Literal["distance"] = "distance"
    routing_mode: Literal["directed", "undirected_prototype"]
    snapped_start: SnappedRoutePoint
    snapped_destination: SnappedRoutePoint
    polyline: list[ScenePoint] = Field(min_length=1)
    distance: float = Field(ge=0.0)
    distance_unit: Literal["m"] = "m"
    estimated_duration: float = Field(ge=0.0)
    duration_unit: Literal["s"] = "s"
    bounds: SceneBounds
    warnings: list[str] = Field(default_factory=list)


__all__ = [
    "CreateRouteRequest",
    "RoutePoint",
    "RouteResponse",
    "SnappedRoutePoint",
]
