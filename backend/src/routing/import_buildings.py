"""Download an area's buildings from OpenStreetMap: each one's shape and where it is. Needs no key.

Run from backend/: python -m src.routing.import_buildings [--area toa-payoh | --all-singapore] [--output PATH]

Shapes are stored as latitude/longitude and as local metres, in the frame the fleet map uses
(x east and y north of central Singapore), ready to extrude. Height is only stored when OSM has it;
most buildings have none, so whoever extrudes them chooses one.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sqlite3
import sys
import time
from datetime import UTC, datetime
from pathlib import Path
from urllib.parse import urlencode
from urllib.request import Request, urlopen

from src.routing.build_singapore_fleet_fixture import DEFAULT_AREA, SERVICE_AREAS, project

OVERPASS_URLS = ("https://overpass-api.de/api/interpreter", "https://overpass.kumi.systems/api/interpreter")
QUERY = """[out:json][timeout:180];
area["ISO3166-1"="SG"][admin_level=2]->.singapore;
(way["building"](area.singapore)({box});relation["building"](area.singapore)({box}););
out body geom;"""
# All of Singapore at once is too much for the public servers, so it is fetched in tiles
SOUTH, WEST, NORTH, EAST = 1.15, 103.59, 1.48, 104.10
ROWS, COLUMNS = 5, 8
ATTEMPTS = 4
ATTRIBUTION = "Buildings (c) OpenStreetMap contributors, available under the Open Database Licence"
OUTPUT_DIRECTORY = Path(__file__).resolve().parents[2] / "fixtures" / "buildings"
SCHEMA = """
CREATE TABLE buildings (
    osm_id TEXT PRIMARY KEY,
    building_type TEXT NOT NULL,
    name TEXT NOT NULL,
    height_m REAL,
    levels REAL,
    center_lat REAL NOT NULL,
    center_lon REAL NOT NULL,
    center_x_m REAL NOT NULL,
    center_y_m REAL NOT NULL,
    outer_rings_latlon TEXT NOT NULL,
    outer_rings_m TEXT NOT NULL
);
CREATE INDEX buildings_position ON buildings (center_lat, center_lon);
CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
"""


METRES_PER_LEVEL = 3.0
# Plausible (lowest, highest) heights for a building OSM gives neither a height nor levels for
HEIGHT_RANGES_M = {"house": (6, 10), "detached": (6, 10), "terrace": (6, 10), "residential": (25, 45), "apartments": (25, 60),
                   "retail": (6, 15), "commercial": (10, 30), "office": (15, 40), "industrial": (8, 18), "school": (10, 18),
                   "garage": (3, 5), "shed": (3, 4), "roof": (4, 6), "parking": (8, 20)}
DEFAULT_HEIGHT_RANGE_M = (8, 20)


def resolved_height(osm_id: str, building_type: str, height_m: float | None, levels: float | None) -> float:
    """The real height if known, else from its floors, else a plausible one the ID makes repeatable."""
    if height_m:
        return height_m
    if levels:
        return levels * METRES_PER_LEVEL
    low, high = HEIGHT_RANGES_M.get(building_type, DEFAULT_HEIGHT_RANGE_M)
    share = int(hashlib.sha256(osm_id.encode()).hexdigest()[:8], 16) / 0xFFFFFFFF
    return round(low + (high - low) * share, 1)


def read_buildings(path: Path) -> list[dict]:
    """An imported area's buildings, each with the height to extrude it to."""
    connection = sqlite3.connect(f"file:{path.as_posix()}?mode=ro", uri=True)
    try:
        rows = connection.execute("SELECT osm_id, building_type, name, height_m, levels, outer_rings_m FROM buildings ORDER BY osm_id").fetchall()
    finally:
        connection.close()
    return [{"id": osm_id, "type": kind, "name": name, "height_m": resolved_height(osm_id, kind, height, levels),
             "rings_m": json.loads(rings)} for osm_id, kind, name, height, levels, rings in rows]


def number(text) -> float | None:
    """The leading number of an OSM value such as "45", "45 m" or "12;14"."""
    match = re.match(r"\s*(\d+(?:\.\d+)?)", str(text or ""))
    return float(match.group(1)) if match else None


def outer_rings(element: dict) -> list[list[tuple[float, float]]]:
    """A building's closed outer outlines as (lat, lon); a relation can have several."""
    if element["type"] == "way":
        ways = [element.get("geometry", [])]
    else:
        ways = [member.get("geometry", []) for member in element.get("members", [])
                if member.get("type") == "way" and member.get("role") == "outer"]
    return [ring for ring in join_pieces([[(point["lat"], point["lon"]) for point in way if point] for way in ways])
            if len(ring) >= 4 and ring[0] == ring[-1]]


def join_pieces(pieces: list[list[tuple[float, float]]]) -> list[list[tuple[float, float]]]:
    """Join ways that share end points: one outline in OSM can be split across several."""
    joined = []
    remaining = [piece for piece in pieces if piece]
    while remaining:
        line = remaining.pop(0)
        grew = True
        while grew and line[0] != line[-1]:
            grew = False
            for piece in remaining:
                if piece[0] == line[-1]:
                    line = line + piece[1:]
                elif piece[-1] == line[-1]:
                    line = line + piece[-2::-1]
                else:
                    continue
                remaining.remove(piece)
                grew = True
                break
        joined.append(line)
    return joined


def building_row(element: dict) -> tuple | None:
    rings = outer_rings(element)
    if not rings:
        return None
    tags = element.get("tags", {})
    points = [point for ring in rings for point in ring[:-1]]
    latitude = sum(point[0] for point in points) / len(points)
    longitude = sum(point[1] for point in points) / len(points)
    center = project(latitude, longitude)
    return (f"{element['type']}/{element['id']}", str(tags.get("building", "yes")), str(tags.get("name", "")),
            number(tags.get("height")), number(tags.get("building:levels")), latitude, longitude, center[0], center[1],
            json.dumps(rings, separators=(",", ":")),
            json.dumps([[[round(c, 2) for c in project(*point)] for point in ring] for ring in rings], separators=(",", ":")))


def write_database(path: Path, elements: list[dict], imported_at: str) -> int:
    """Replace the database at once, so a failed import leaves the previous one intact."""
    path.parent.mkdir(parents=True, exist_ok=True)
    partial = path.with_name(path.name + ".partial")
    partial.unlink(missing_ok=True)
    connection = sqlite3.connect(partial)
    try:
        connection.executescript(SCHEMA)
        rows = [row for row in map(building_row, elements) if row is not None]
        connection.executemany("INSERT OR REPLACE INTO buildings VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)", rows)
        connection.executemany("INSERT INTO metadata VALUES (?, ?)", [("imported_at", imported_at), ("attribution", ATTRIBUTION)])
        connection.commit()
    finally:
        connection.close()
    partial.replace(path)
    return len(rows)


def tiles() -> list[str]:
    height, width = (NORTH - SOUTH) / ROWS, (EAST - WEST) / COLUMNS
    return [f"{SOUTH + row * height:.4f},{WEST + column * width:.4f},{SOUTH + (row + 1) * height:.4f},{WEST + (column + 1) * width:.4f}"
            for row in range(ROWS) for column in range(COLUMNS)]


def fetch_tile(box: str) -> list[dict]:
    """One tile's elements, trying each server in turn. Raises if every attempt fails."""
    error = None
    for attempt in range(ATTEMPTS):
        url = OVERPASS_URLS[attempt % len(OVERPASS_URLS)]
        request = Request(url, data=urlencode({"data": QUERY.format(box=box)}).encode(), headers={"User-Agent": "Formula9-FleetMap/1.0"})
        try:
            with urlopen(request, timeout=240) as response:
                body = json.load(response)
            # A timeout can arrive as a complete but cut-short answer, with only this remark to say so
            if "remark" in body and "runtime error" in body["remark"]:
                raise ValueError(body["remark"])
            return body["elements"]
        except (OSError, ValueError, KeyError) as failure:
            error = failure
            time.sleep(15 * (attempt + 1))
    raise OSError(f"tile {box}: {error}")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--area", choices=sorted(SERVICE_AREAS), default=DEFAULT_AREA, help="Service area to download")
    parser.add_argument("--all-singapore", action="store_true", help="The whole island, tile by tile; slow on public servers")
    parser.add_argument("--output", type=Path, default=None, help="SQLite file to write")
    arguments = parser.parse_args(argv)
    name = "singapore" if arguments.all_singapore else arguments.area
    output = arguments.output or OUTPUT_DIRECTORY / f"{name}.sqlite"
    # A building crossing a tile edge comes back in both tiles
    elements = {}
    boxes = tiles() if arguments.all_singapore else [",".join(str(value) for value in SERVICE_AREAS[arguments.area]["box"])]
    for index, box in enumerate(boxes, 1):
        try:
            found = fetch_tile(box)
        except OSError as error:
            print(f"Could not download buildings; nothing was written. {error}", file=sys.stderr)
            return 1
        elements.update({(element["type"], element["id"]): element for element in found})
        print(f"Tile {index}/{len(boxes)}: {len(found)} found, {len(elements)} in total", flush=True)
        time.sleep(2)
    count = write_database(output, list(elements.values()), datetime.now(UTC).isoformat(timespec="seconds"))
    print(f"Wrote {count} buildings to {output} from {len(elements)} OSM elements")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
