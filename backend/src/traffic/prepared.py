"""The prepared traffic files that the city view's road and traffic routes share."""

from __future__ import annotations

import json
from functools import lru_cache

from src.routing.build_singapore_fleet_fixture import SERVICE_AREAS
from src.traffic.import_vehicle_population import OUTPUT_DIRECTORY


@lru_cache(maxsize=4)
def _read(area: str) -> dict:
    return json.loads((OUTPUT_DIRECTORY / f"{area}.json").read_text(encoding="utf-8"))


def prepared_area(area: str) -> dict | None:
    """An area's prepared file, or None if there is none."""
    # Only a known area's name, so the path cannot leave the folder or reach its other files
    if area not in SERVICE_AREAS or not (OUTPUT_DIRECTORY / f"{area}.json").is_file():
        return None
    return _read(area)
