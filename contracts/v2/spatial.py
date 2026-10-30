"""Opt-in v2.1 spatial contract. Legacy v2 snapshots never imply 3D readiness.

Matrices are row-major, act on column vectors, and use metres. Render bytes
remain glTF Y-up; body/world state is right-handed X-forward/Y-left/Z-up.
"""

from __future__ import annotations

import math
from pathlib import PurePosixPath
from typing import Annotated, Literal

from pydantic import BaseModel, ConfigDict, Field, FiniteFloat, StringConstraints, field_validator, model_validator

Id = Annotated[str, StringConstraints(min_length=1, max_length=128, pattern=r"^[A-Za-z0-9][A-Za-z0-9_.:-]*$")]
Revision = Annotated[int, Field(ge=0)]


class WireModel(BaseModel):
    model_config = ConfigDict(extra="allow", strict=True)

SPATIAL_VERSION = "1.0"
ASSET_PROFILE = "formula9-gltf2-r1"
MAX_ASSET_BYTES = 2 * 1024 * 1024
MAX_DECODED_BYTES = 8 * 1024 * 1024
MAX_VERTICES = 65536
MAX_INDICES = 196608
MAX_TEXTURE_DIMENSION = 1024
Vec3 = Annotated[list[FiniteFloat], Field(min_length=3, max_length=3)]
Quaternion = Annotated[list[FiniteFloat], Field(min_length=4, max_length=4)]
Matrix = Annotated[list[FiniteFloat], Field(min_length=16, max_length=16)]
Digest = Annotated[str, StringConstraints(pattern=r"^[a-f0-9]{64}$")]


def unit_quaternion(value: list[float]) -> list[float]:
    if abs(sum(v * v for v in value) - 1) > 1e-6:
        raise ValueError("NON_UNIT_QUATERNION")
    return value


def safe_asset_path(value: str) -> str:
    path = PurePosixPath(value)
    if (not value or len(value) > 160 or "\\" in value or ":" in value or "%" in value
            or path.is_absolute() or any(p in {"", ".", ".."} for p in value.split("/"))
            or path.suffix != ".glb"):
        raise ValueError("UNSAFE_ASSET_PATH")
    return value


def rigid_matrix(value: list[float]) -> list[float]:
    if any(abs(value[12 + i] - x) > 1e-9 for i, x in enumerate([0, 0, 0, 1])):
        raise ValueError("NON_AFFINE_MATRIX")
    columns = [[value[r * 4 + c] for r in range(3)] for c in range(3)]
    if any(abs(sum(a * b for a, b in zip(columns[i], columns[j])) - (1 if i == j else 0)) > 1e-6
           for i in range(3) for j in range(3)):
        raise ValueError("ASSET_SCALE_OR_SHEAR")
    a, b, c = columns
    det = a[0] * (b[1] * c[2] - b[2] * c[1]) - b[0] * (a[1] * c[2] - a[2] * c[1]) + c[0] * (a[1] * b[2] - a[2] * b[1])
    if abs(det - 1) > 1e-6:
        raise ValueError("REFLECTED_ASSET_BASIS")
    return value


class BodyPose(WireModel):
    position_m: Vec3
    orientation_xyzw: Quaternion

    _unit = field_validator("orientation_xyzw")(unit_quaternion)


class SpatialVehicle(BodyPose):
    """Origin is the collision body's centre; never a render mesh pivot."""

    dimensions_m: Vec3
    collision_proxy_id: Id
    collision_proxy_revision: Id
    support_surface_id: Id
    lane_id: Id
    deck_id: Id
    support_offset_m: Annotated[FiniteFloat, Field(ge=0)]
    route_progress_m: Annotated[FiniteFloat, Field(ge=0)]
    movement: Literal["CONTINUOUS", "HELD", "DISCONTINUITY"]
    render_asset_id: Id
    render_asset_sha256: Digest
    asset_to_body: Matrix

    _rigid = field_validator("asset_to_body")(rigid_matrix)

    @field_validator("dimensions_m")
    @classmethod
    def positive(cls, value: list[float]) -> list[float]:
        if any(v <= 0 or v > 1000 for v in value):
            raise ValueError("INVALID_BODY_DIMENSIONS")
        return value


class SpatialReference(WireModel):
    spatial_schema_version: Literal["1.0"]
    scene_id: Id
    scene_revision: Id
    spatial_revision: Id
    transform_revision: Id
    manifest_sha256: Digest
    required_capabilities: list[Literal["world_3d"]] = Field(min_length=1, max_length=1)


class RenderAsset(WireModel):
    asset_id: Id
    path: Annotated[str, Field(min_length=1, max_length=160)]
    sha256: Digest
    byte_length: Annotated[int, Field(gt=0, le=MAX_ASSET_BYTES)]
    decoded_bytes: Annotated[int, Field(gt=0, le=MAX_DECODED_BYTES)]
    bounds_min_m: Vec3
    bounds_max_m: Vec3
    mandatory: bool
    fallback_asset_id: Id | None = None
    license: Literal["CC0-1.0"]
    attribution: Annotated[str, Field(min_length=1, max_length=512)]

    _safe = field_validator("path")(safe_asset_path)

    @model_validator(mode="after")
    def valid_bounds(self) -> "RenderAsset":
        if any(lo >= hi for lo, hi in zip(self.bounds_min_m, self.bounds_max_m)):
            raise ValueError("INVALID_ASSET_BOUNDS")
        if self.mandatory and self.fallback_asset_id is not None:
            raise ValueError("MANDATORY_ASSET_CANNOT_FALL_BACK")
        return self


class Proxy(BodyPose):
    proxy_id: Id
    revision: Id
    entity_id: Id
    classification: Literal["ROAD_SUPPORT", "BLOCKING", "NON_OPERATIONAL"]
    dimensions_m: Vec3

    _positive = field_validator("dimensions_m")(SpatialVehicle.positive.__func__)


class SceneEntity(BodyPose):
    entity_id: Id
    kind: Literal["ROAD", "BUILDING", "STOP", "DEPOT", "CHARGER", "SHUTTLE", "OBSTRUCTION"]
    dimensions_m: Vec3
    render_asset_id: Id
    proxy_id: Id
    asset_to_body: Matrix

    _positive = field_validator("dimensions_m")(SpatialVehicle.positive.__func__)
    _rigid = field_validator("asset_to_body")(rigid_matrix)


class RoadSurface(WireModel):
    surface_id: Id
    entity_id: Id
    lane_id: Id
    deck_id: Id
    directed_edge_id: Id
    width_m: Annotated[FiniteFloat, Field(gt=0)]
    path_samples_m: Annotated[list[Vec3], Field(min_length=2, max_length=1024)]
    normal: Vec3
    permitted_next_surface_ids: Annotated[list[Id], Field(max_length=16)]
    clearance_m: Annotated[FiniteFloat, Field(gt=0)]

    @model_validator(mode="after")
    def valid_surface(self) -> "RoadSurface":
        if abs(sum(v*v for v in self.normal) - 1) > 1e-6 or self.normal[2] <= 0:
            raise ValueError("INVALID_SUPPORT_NORMAL")
        for a, b in zip(self.path_samples_m, self.path_samples_m[1:]):
            delta = [y-x for x, y in zip(a, b)]
            if math.dist(a, b) < 1e-6 or abs(sum(x*y for x, y in zip(delta, self.normal))) > 1e-5:
                raise ValueError("INVALID_SURFACE_PATH")
        return self


class CameraBookmark(WireModel):
    camera_id: Id
    position_m: Vec3
    target_m: Vec3


class TrainingSceneRevision(WireModel):
    contract_version: Literal["2.1"]
    spatial_schema_version: Literal["1.0"]
    asset_profile: Literal["formula9-gltf2-r1"]
    scene_id: Id
    scene_revision: Id
    spatial_revision: Id
    transform_revision: Id
    parent_scene_revision: Id | None
    units: Literal["metres"]
    world_basis: Literal["RH_X_EAST_Y_NORTH_Z_UP"]
    geographic_origin: Vec3  # latitude, longitude, altitude; never added to local poses
    local_origin_m: Vec3
    source_geography: Annotated[str, Field(min_length=1, max_length=512)]
    provenance: Literal["AUTHORED_TRAINING_BLOCKOUT"]
    build_tool: Literal["formula9-scene-builder-1.0"]
    importer_profile: Literal["formula9-glb-validator-1.0"]
    assets: Annotated[list[RenderAsset], Field(min_length=1, max_length=64)]
    entities: Annotated[list[SceneEntity], Field(min_length=1, max_length=256)]
    proxies: Annotated[list[Proxy], Field(min_length=1, max_length=256)]
    road_surfaces: Annotated[list[RoadSurface], Field(min_length=1, max_length=64)]
    camera_bookmarks: Annotated[list[CameraBookmark], Field(min_length=1, max_length=16)]

    @model_validator(mode="after")
    def bindings(self) -> "TrainingSceneRevision":
        def unique(items, key):
            result = {getattr(x, key): x for x in items}
            if len(result) != len(items):
                raise ValueError("DUPLICATE_SEMANTIC_ID")
            return result
        assets = unique(self.assets, "asset_id")
        entities = unique(self.entities, "entity_id")
        proxies = unique(self.proxies, "proxy_id")
        surfaces = unique(self.road_surfaces, "surface_id")
        unique(self.camera_bookmarks, "camera_id")
        if len({a.path for a in self.assets}) != len(self.assets):
            raise ValueError("DUPLICATE_ASSET_PATH")
        for a in self.assets:
            if not a.mandatory and (a.fallback_asset_id not in assets or not assets[a.fallback_asset_id].mandatory):
                raise ValueError("MISSING_MANDATORY_FALLBACK")
            if a.fallback_asset_id:
                fallback = assets[a.fallback_asset_id]
                if a.bounds_min_m != fallback.bounds_min_m or a.bounds_max_m != fallback.bounds_max_m:
                    raise ValueError("FALLBACK_DIMENSIONS_MISMATCH")
        for e in self.entities:
            if e.render_asset_id not in assets or e.proxy_id not in proxies:
                raise ValueError("MISSING_ENTITY_BINDING")
            p = proxies[e.proxy_id]
            if p.entity_id != e.entity_id or p.dimensions_m != e.dimensions_m or p.position_m != e.position_m or p.orientation_xyzw != e.orientation_xyzw:
                raise ValueError("RENDER_PROXY_MISMATCH")
        for p in self.proxies:
            if p.entity_id not in entities or entities[p.entity_id].proxy_id != p.proxy_id:
                raise ValueError("UNBOUND_PROXY")
        for s in self.road_surfaces:
            if s.entity_id not in entities or entities[s.entity_id].kind != "ROAD" or proxies[entities[s.entity_id].proxy_id].classification != "ROAD_SUPPORT":
                raise ValueError("INVALID_SUPPORT_BINDING")
            if any(i not in surfaces for i in s.permitted_next_surface_ids):
                raise ValueError("MISSING_SURFACE_TRANSITION")
            validate_surface_support(s, entities[s.entity_id])
        return self


class SceneCapabilities(SpatialReference):
    contract_version: Literal["2.1"]
    asset_profile: Literal["formula9-gltf2-r1"]
    world_3d: Literal[True]
    authoritative_3d_motion: bool
    local_manoeuvre: Literal[False]
    recorded_perception: Literal[False]
    max_asset_bytes: Literal[2097152] = MAX_ASSET_BYTES
    max_decoded_bytes: Literal[8388608] = MAX_DECODED_BYTES
    max_texture_dimension: Literal[1024] = MAX_TEXTURE_DIMENSION
    max_decode_jobs: Literal[2] = 2
    max_queued_decode_jobs: Literal[4] = 4


class ReadinessRequest(SpatialReference):
    asset_profile: Literal["formula9-gltf2-r1"]
    render_ready: bool
    asset_hashes: Annotated[dict[Id, Digest], Field(max_length=64)]


def world_to_render(v: list[float]) -> list[float]:
    return [v[0], v[2], -v[1]]


def render_to_world(v: list[float]) -> list[float]:
    return [v[0], -v[2], v[1]]


def orientation_to_render(q: list[float]) -> list[float]:
    # Conjugation by B (rotation -90 degrees about X), not Euler reordering.
    return [q[0], q[2], -q[1], q[3]]


def rotate(q: list[float], v: list[float]) -> list[float]:
    x, y, z, w = q
    tx, ty, tz = 2*(y*v[2]-z*v[1]), 2*(z*v[0]-x*v[2]), 2*(x*v[1]-y*v[0])
    return [v[0]+w*tx+y*tz-z*ty, v[1]+w*ty+z*tx-x*tz, v[2]+w*tz+x*ty-y*tx]


def validate_surface_support(surface: RoadSurface, entity: SceneEntity) -> None:
    """M2 surfaces are strips on the local +Z face of a rigid road slab.

    Check the whole width of every segment, not just its centreline. Curved
    paths may use multiple segments, each contained in the declared support.
    """
    tolerance = 1e-4  # metres, shared with the web and native ingress checks
    norm = math.sqrt(sum(v*v for v in entity.orientation_xyzw))
    q = [v/norm for v in entity.orientation_xyzw]
    normal = rotate(q, [0., 0., 1.])
    if any(abs(a-b) > 1e-6 for a, b in zip(normal, surface.normal)):
        raise ValueError("SUPPORT_NORMAL_MISMATCH")
    inverse = [-q[0], -q[1], -q[2], q[3]]
    half = [v/2 for v in entity.dimensions_m]
    points = []
    for sample in surface.path_samples_m:
        point = rotate(inverse, [a-b for a, b in zip(sample, entity.position_m)])
        if (not all(math.isfinite(v) for v in point) or abs(point[2]-half[2]) > tolerance
                or any(abs(point[c]) > half[c]+tolerance for c in range(2))):
            raise ValueError("SURFACE_OUTSIDE_SUPPORT")
        points.append(point)
    for a, b in zip(points, points[1:]):
        dx, dy = b[0]-a[0], b[1]-a[1]
        length = math.hypot(dx, dy)
        if length < 1e-6:
            raise ValueError("INVALID_SURFACE_PATH")
        margin = [surface.width_m/2*abs(dy/length), surface.width_m/2*abs(dx/length)]
        if any(abs(p[c])+margin[c] > half[c]+tolerance for p in (a, b) for c in range(2)):
            raise ValueError("SURFACE_WIDTH_OUTSIDE_SUPPORT")


def render_asset_point(pose: BodyPose, matrix: list[float], vertex: list[float]) -> list[float]:
    body = [sum(matrix[r*4+c]*vertex[c] for c in range(3)) + matrix[r*4+3] for r in range(3)]
    world = [a+b for a, b in zip(pose.position_m, rotate(pose.orientation_xyzw, body))]
    return world_to_render(world)
