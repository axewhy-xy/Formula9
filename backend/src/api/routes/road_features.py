"""Traffic lights, crossings and road markings for the 3D city view, from data imported from LTA DataMall."""

import json
import time
from functools import lru_cache

from fastapi import APIRouter, HTTPException

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
