"""Road centrelines and lane counts for the 3D city view, from the area's prepared traffic data."""

from fastapi import APIRouter, HTTPException

from src.traffic.import_vehicle_population import ROADS_ATTRIBUTION
from src.traffic.prepared import prepared_area

router = APIRouter(prefix="/roads", tags=["roads"])


@router.get("/{area}")
def roads(area: str) -> dict:
    traffic = prepared_area(area)
    if traffic is None:
        raise HTTPException(status_code=404, detail=f"No road data for {area}")
    return {"area": area, "attribution": ROADS_ATTRIBUTION, "roads": traffic["roads"]}
