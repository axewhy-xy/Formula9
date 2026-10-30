"""Singapore bus stops, services and routes imported from LTA DataMall."""

from fastapi import APIRouter, Depends, HTTPException, Query

from src.core.config import get_settings
from src.transit.store import TransitStore, TransitUnavailable

router = APIRouter(prefix="/transit", tags=["transit"])

# Singapore with a margin; a box outside it can only ever be empty
_SOUTH, _WEST, _NORTH, _EAST = 1.1, 103.5, 1.5, 104.15
MAX_STOPS = 2000


def get_transit_store() -> TransitStore:
    return TransitStore(get_settings().transit_database_path)


def _unavailable(error: TransitUnavailable) -> HTTPException:
    return HTTPException(status_code=503, detail=str(error))


@router.get("/bus-stops")
def bus_stops(south: float = Query(..., ge=-90, le=90), west: float = Query(..., ge=-180, le=180),
              north: float = Query(..., ge=-90, le=90), east: float = Query(..., ge=-180, le=180),
              limit: int = Query(1000, ge=1, le=MAX_STOPS),
              store: TransitStore = Depends(get_transit_store)) -> dict:
    if south >= north or west >= east:
        raise HTTPException(status_code=422, detail="Box needs south < north and west < east")
    if north < _SOUTH or south > _NORTH or east < _WEST or west > _EAST:
        return {"stops": [], "truncated": False, "imported_at": None, "attribution": None}
    try:
        return store.stops_in(south, west, north, east, limit)
    except TransitUnavailable as error:
        raise _unavailable(error) from error


@router.get("/bus-services")
def bus_services(store: TransitStore = Depends(get_transit_store)) -> dict:
    try:
        return store.services()
    except TransitUnavailable as error:
        raise _unavailable(error) from error


@router.get("/bus-services/{service_no}/stops")
def bus_service_stops(service_no: str, direction: int = Query(1, ge=1, le=2),
                      store: TransitStore = Depends(get_transit_store)) -> dict:
    try:
        route = store.route(service_no, direction)
    except TransitUnavailable as error:
        raise _unavailable(error) from error
    if route is None:
        raise HTTPException(status_code=404, detail=f"No bus service {service_no} in direction {direction}")
    return route
