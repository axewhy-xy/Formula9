"""Prepared background traffic for the 3D city view."""

from fastapi import APIRouter, HTTPException

from src.traffic.prepared import prepared_area

router = APIRouter(prefix="/traffic", tags=["traffic"])


@router.get("/{area}")
def traffic(area: str) -> dict:
    prepared = prepared_area(area)
    if prepared is None:
        raise HTTPException(status_code=404, detail=f"No traffic data for {area}")
    return {
        "area": area,
        "attribution": prepared["attribution"],
        "models": prepared["models"],
        "vehicles": prepared["vehicles"],
    }
