"""Traffic lights, crossings, road markings and sidewalks for the 3D city view, from data imported from LTA DataMall and OpenStreetMap."""

import gzip
import json
import time
from functools import lru_cache

from fastapi import APIRouter, HTTPException, Request, Response

from src.traffic.import_road_features import output_path
from src.traffic.signal_cycle import junction_states

router = APIRouter(prefix="/road-features", tags=["road-features"])


@lru_cache(maxsize=12)
def _file(area: str, kind: str) -> dict:
    return json.loads(output_path(area, kind).read_text(encoding="utf-8"))


def _feature(area: str, kind: str) -> dict:
    # Only an imported file's own name, so the path cannot leave the folder
    if not area.replace("-", "").isalnum() or not output_path(area, kind).is_file():
        raise HTTPException(status_code=404, detail=f"No {kind} data for {area}")
    return _file(area, kind)


@router.get("/{area}/signals")
def signals(area: str) -> dict:
    return _feature(area, "signals")


@router.get("/{area}/signals/state")
def signal_state(area: str, t: float | None = None) -> dict:
    """Every junction's simulated colours at t seconds; by default at the current time."""
    data = _feature(area, "signals")
    now = time.time() if t is None else t
    return {"t": now, "simulated": True, "junctions": junction_states(data["junctions"], now)}


@router.get("/{area}/crossings")
def crossings(area: str) -> dict:
    return _feature(area, "crossings")


@router.get("/{area}/markings")
def markings(area: str) -> dict:
    return _feature(area, "markings")


@lru_cache(maxsize=4)
def _walkways_bytes(area: str) -> tuple[bytes, bytes]:
    """The walkways file as it is on disk, and gzipped once, as it runs to a megabyte or more."""
    raw = output_path(area, "walkways").read_bytes()
    return raw, gzip.compress(raw, compresslevel=6, mtime=0)


@router.get("/{area}/walkways")
def walkways(area: str, request: Request) -> Response:
    """Sidewalk triangles, kerbs and ramps (src.traffic.import_walkways). Sent gzipped to a client that takes it."""
    if not area.replace("-", "").isalnum() or not output_path(area, "walkways").is_file():
        raise HTTPException(status_code=404, detail=f"No walkways data for {area}")
    raw, packed = _walkways_bytes(area)
    if "gzip" in request.headers.get("accept-encoding", "").lower():
        return Response(packed, media_type="application/json", headers={"Content-Encoding": "gzip", "Vary": "Accept-Encoding"})
    return Response(raw, media_type="application/json", headers={"Vary": "Accept-Encoding"})
