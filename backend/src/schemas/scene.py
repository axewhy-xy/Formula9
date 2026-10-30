"""Renderer-neutral static scene REST models."""

from pydantic import BaseModel, Field

from src.schemas.simulation import CoordinatesMetadata


SCENE_TRANSFORM_REVISION = "wgs84-linear-enu-v1"


class SceneBounds(BaseModel):
    """Axis-aligned bounds in the canonical x-east/y-north metre frame."""

    min_x: float
    min_y: float
    max_x: float
    max_y: float


class SceneCounts(BaseModel):
    roads: int = 0
    buildings: int = 0
    pois: int = 0


class SceneMetadataResponse(BaseModel):
    scene_id: str
    scene_revision: str
    transform_revision: str = SCENE_TRANSFORM_REVISION
    contract_version: str = "1.0"
    coordinates: CoordinatesMetadata
    bounds: SceneBounds
    counts: SceneCounts


class ScenePoint(BaseModel):
    x: float
    y: float
    z: float = 0.0


class RoadFeature(BaseModel):
    road_id: int
    highway_type: str
    points: list[ScenePoint] = Field(min_length=2)


class BuildingFeature(BaseModel):
    building_id: int
    building_type: str
    points: list[ScenePoint] = Field(min_length=3)


class PoiFeature(BaseModel):
    poi_id: int
    poi_type: str
    poi_value: str
    name: str | None = None
    position: ScenePoint


class SceneChunkResponse(BaseModel):
    scene_id: str
    scene_revision: str
    transform_revision: str = SCENE_TRANSFORM_REVISION
    contract_version: str = "1.0"
    coordinates: CoordinatesMetadata
    requested_bounds: SceneBounds
    lod: int = Field(ge=0, le=3)
    roads: list[RoadFeature]
    buildings: list[BuildingFeature]
    pois: list[PoiFeature]
