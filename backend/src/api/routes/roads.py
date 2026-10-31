"""Road centrelines and lane counts for the 3D city view, from the area's prepared traffic data."""

import json
from functools import lru_cache

from fastapi import APIRouter, HTTPException

from src.routing.build_singapore_fleet_fixture import SERVICE_AREAS
from src.traffic.import_vehicle_population import OUTPUT_DIRECTORY, ROADS_ATTRIBUTION

router = APIRouter(prefix="/roads", tags=["roads"])


@lru_cache(maxsize=4)
def _area(area: str) -> dict:
    traffic = json.loads((OUTPUT_DIRECTORY / f"{area}.json").read_text(encoding="utf-8"))
    return {"area": area, "attribution": ROADS_ATTRIBUTION, "roads": traffic["roads"]}


@router.get("/{area}")
def roads(area: str) -> dict:
    # Only a known area's name, so the path cannot leave the folder or reach its other files
    if area not in SERVICE_AREAS or not (OUTPUT_DIRECTORY / f"{area}.json").is_file():
        raise HTTPException(status_code=404, detail=f"No road data for {area}")
    return _area(area)
