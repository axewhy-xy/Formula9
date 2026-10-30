"""Download every Singapore bus stop from OpenStreetMap into the transit database. Needs no key.

Run from backend/: python -m src.transit.import_osm [--output PATH]

OSM carries each stop's LTA code as ``ref``. It has no bus services or routes; those need a
DataMall key and src.transit.import_lta, which replaces this data when run.
"""

from __future__ import annotations

import argparse
import json
import sys
from datetime import UTC, datetime
from pathlib import Path
from urllib.parse import urlencode
from urllib.request import Request, urlopen

from src.core.config import get_settings
from src.transit.store import write_database

OVERPASS_URL = "https://overpass-api.de/api/interpreter"
QUERY = """[out:json][timeout:120];
area["ISO3166-1"="SG"][admin_level=2]->.singapore;
node["highway"="bus_stop"](area.singapore);
out body;"""
ATTRIBUTION = "Bus stops (c) OpenStreetMap contributors, available under the Open Database Licence"


def stop_rows(elements: list[dict]) -> list[dict]:
    """Stops in the DataMall row shape; one without a stop code cannot be identified, so it is left out."""
    rows = []
    for element in elements:
        tags = element.get("tags", {})
        code = str(tags.get("ref", "")).strip()
        if code:
            rows.append({"BusStopCode": code, "Description": tags.get("name", ""), "RoadName": tags.get("addr:street", ""),
                         "Latitude": element.get("lat"), "Longitude": element.get("lon")})
    return rows


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--output", type=Path, default=None, help="SQLite file to write")
    arguments = parser.parse_args(argv)
    request = Request(OVERPASS_URL, data=urlencode({"data": QUERY}).encode(), headers={"User-Agent": "Formula9-FleetMap/1.0"})
    try:
        with urlopen(request, timeout=180) as response:
            elements = json.load(response)["elements"]
    except (OSError, ValueError, KeyError) as error:
        print(f"Could not download bus stops from Overpass: {error}", file=sys.stderr)
        return 1
    output = arguments.output or get_settings().transit_database_path
    counts = write_database(output, stop_rows(elements), [], [], datetime.now(UTC).isoformat(timespec="seconds"), ATTRIBUTION)
    print(f"Wrote {counts['stops']} stops to {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
