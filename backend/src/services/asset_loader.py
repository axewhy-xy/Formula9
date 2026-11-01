"""Load and validate the authored route fixture outside the simulation step."""

import json
import math
from pathlib import Path

from pydantic import TypeAdapter

from contracts.v2.models import Id
from ..schemas.shuttle_data import Vector2D

DEFAULT_ROUTES_PATH = Path(__file__).resolve().parents[2] / "fixtures" / "routes_testing.json"
# One per shuttle at most
MAX_ROUTES = 5
MAX_WAYPOINTS = 64
# An approved route follows real roads junction to junction, so it has far more corners than an asset.
MAX_ROUTE_WAYPOINTS = 2048
MAX_ASSET_BYTES = 64 * 1024
MAX_COORDINATE_M = 1_000_000.0


def validate_routes(data: list[dict], max_waypoints: int = MAX_WAYPOINTS) -> list[dict]:
    """Copy assets and derive lengths from geometry rather than rounded metadata."""
    if not isinstance(data, list) or not 1 <= len(data) <= MAX_ROUTES:
        raise ValueError(f"The curated fixture requires 1 to {MAX_ROUTES} routes")
    routes = []
    identifiers = set()
    for route in data:
        if not isinstance(route, dict) or not {"route_ID", "waypoints"} <= route.keys():
            raise ValueError("Routes require route_ID and waypoints")
        identifier = route["route_ID"]
        if isinstance(identifier, bool) or not isinstance(identifier, (int, str)):
            raise ValueError("route_ID must be an integer or string")
        TypeAdapter(Id).validate_python(str(identifier))
        if str(identifier) in identifiers:
            raise ValueError("Route IDs must be unique")
        identifiers.add(str(identifier))
        waypoints = route["waypoints"]
        if not isinstance(waypoints, list) or not 2 <= len(waypoints) <= max_waypoints:
            raise ValueError(f"Routes require 2 to {max_waypoints} waypoints")
        points = [Vector2D.model_validate(p).model_copy(deep=True) for p in waypoints]
        if any(abs(p.x) > MAX_COORDINATE_M or abs(p.y) > MAX_COORDINATE_M for p in points):
            raise ValueError("Route coordinates exceed the curated local area")
        lengths = [math.hypot(b.x - a.x, b.y - a.y) for a, b in zip(points, points[1:])]
        if any(length <= 0 or not math.isfinite(length) for length in lengths):
            raise ValueError("Route segments must have positive finite length")
        total = sum(lengths)
        if not math.isfinite(total):
            raise ValueError("Route length must be finite")
        routes.append({"route_ID": identifier, "waypoints": points, "total_length": total})
    return routes


def load_routes_from_json(file_path: str | Path | None = None) -> list[dict]:
    """Missing or malformed requested assets fail explicitly; never substitute a scenario."""
    path = DEFAULT_ROUTES_PATH if file_path is None else Path(file_path)
    with path.open("rb") as source:
        contents = source.read(MAX_ASSET_BYTES + 1)
    if len(contents) > MAX_ASSET_BYTES:
        raise ValueError(f"Route assets must not exceed {MAX_ASSET_BYTES} bytes")
    return validate_routes(json.loads(contents))
