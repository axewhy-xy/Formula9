"""Building shapes and heights for the 3D city view, from data imported from OpenStreetMap."""

from functools import lru_cache

from fastapi import APIRouter, HTTPException

from src.routing.import_buildings import ATTRIBUTION, OUTPUT_DIRECTORY, read_buildings

router = APIRouter(prefix="/buildings", tags=["buildings"])


@lru_cache(maxsize=4)
def _area(area: str) -> dict:
    return {"area": area, "attribution": ATTRIBUTION, "buildings": read_buildings(OUTPUT_DIRECTORY / f"{area}.sqlite")}


@router.get("/{area}")
def buildings(area: str) -> dict:
    # Only an imported file's own name, so the path cannot leave the folder
    if not area.replace("-", "").isalnum() or not (OUTPUT_DIRECTORY / f"{area}.sqlite").is_file():
        raise HTTPException(status_code=404, detail=f"No building data for {area}")
    return _area(area)
