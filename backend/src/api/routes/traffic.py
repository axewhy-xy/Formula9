"""Prepared background traffic for the 3D city view."""

import json
from functools import lru_cache

from fastapi import APIRouter, HTTPException

from src.routing.build_singapore_fleet_fixture import SERVICE_AREAS
from src.traffic.import_vehicle_population import OUTPUT_DIRECTORY

router = APIRouter(prefix="/traffic", tags=["traffic"])


@lru_cache(maxsize=4)
def _area(area: str) -> dict:
    traffic = json.loads((OUTPUT_DIRECTORY / f"{area}.json").read_text(encoding="utf-8"))
    return {
        "area": area,
        "attribution": traffic["attribution"],
        "models": traffic["models"],
        "vehicles": traffic["vehicles"],
    }


@router.get("/{area}")
def traffic(area: str) -> dict:
    # Only a known area's name, so the path cannot leave the folder or reach its other files
    if area not in SERVICE_AREAS or not (OUTPUT_DIRECTORY / f"{area}.json").is_file():
        raise HTTPException(status_code=404, detail=f"No traffic data for {area}")
    return _area(area)
