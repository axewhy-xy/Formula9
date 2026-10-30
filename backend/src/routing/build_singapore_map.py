"""Build a whole-island Singapore road database in the same schema as Phase5_Map/map.db.

Run from backend/: python -m src.routing.build_singapore_map [--output PATH] [--overpass URL]

Drivable OpenStreetMap ways come from the Overpass API (no key needed; one download of
roughly 100 MB, fetched in pieces). If the LTA transit database has been imported, its bus stops fill the
bus_stops table. Point MAP_DATABASE_PATH at the result to route on Singapore roads; the
default stays the Karlsruhe extract that the KITTI recordings line up with.
"""

from __future__ import annotations

import argparse
import json
import sqlite3
import sys
import time
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen

from src.core.config import get_settings

OVERPASS_URL = "https://overpass-api.de/api/interpreter"
# The same road kinds the route planner treats as drivable (road_graph._DRIVABLE_HIGHWAY_TYPES).
# Car-park aisles and driveways are left out: they are a large share of service ways and no
# shuttle routes through them.
# Singapore's bounding box, cut into a grid: one whole-island query times out on the public
# servers. Each piece is still clipped to the country, so Johor and Batam roads stay out.
SOUTH, WEST, NORTH, EAST = 1.15, 103.59, 1.48, 104.10
GRID_ROWS, GRID_COLUMNS = 3, 4
QUERY = """
[out:json][timeout:600];
area["ISO3166-1"="SG"][admin_level=2]->.singapore;
way["highway"~"^(motorway|motorway_link|trunk|trunk_link|primary|primary_link|secondary|secondary_link|tertiary|tertiary_link|unclassified|residential|living_street|service|road)$"]
   ["service"!~"^(parking_aisle|driveway)$"](area.singapore)({south},{west},{north},{east});
out body;
>;
out skel qt;
"""
TAG_COLUMNS = ("name", "oneway", "maxspeed", "access", "vehicle", "motor_vehicle", "motorcar")

SCHEMA = f"""
CREATE TABLE nodes (node_id INTEGER PRIMARY KEY, lat REAL NOT NULL, lon REAL NOT NULL);
CREATE TABLE roads (road_id INTEGER PRIMARY KEY, highway_type TEXT NOT NULL,
                    {", ".join(f"{column} TEXT" for column in TAG_COLUMNS)});
CREATE TABLE road_nodes (road_id INTEGER NOT NULL, node_id INTEGER NOT NULL, sequence INTEGER NOT NULL);
CREATE TABLE buildings (building_id INTEGER PRIMARY KEY, building_type TEXT NOT NULL);
CREATE TABLE building_nodes (building_id INTEGER NOT NULL, node_id INTEGER NOT NULL, sequence INTEGER NOT NULL,
                             lat REAL NOT NULL, lon REAL NOT NULL);
CREATE TABLE pois (poi_id INTEGER PRIMARY KEY, lat REAL NOT NULL, lon REAL NOT NULL, poi_type TEXT NOT NULL,
                   poi_value TEXT NOT NULL, name TEXT);
CREATE TABLE bus_stops (busStop_id INTEGER PRIMARY KEY, name TEXT, lat REAL NOT NULL, lon REAL NOT NULL);
CREATE INDEX road_nodes_road ON road_nodes (road_id, sequence);
CREATE VIRTUAL TABLE nodes_rtree USING rtree(node_id, min_lon, max_lon, min_lat, max_lat);
CREATE VIRTUAL TABLE buildings_rtree USING rtree(building_id, min_lon, max_lon, min_lat, max_lat);
CREATE VIRTUAL TABLE pois_rtree USING rtree(poi_id, min_lon, max_lon, min_lat, max_lat);
"""


def _download_piece(overpass_url: str, box: tuple[float, float, float, float]) -> list[dict]:
    south, west, north, east = box
    query = QUERY.format(south=south, west=west, north=north, east=east)
    request = Request(overpass_url, data=urlencode({"data": query}).encode(),
                      headers={"User-Agent": "Formula9-training-map/1.0"})
    for attempt in range(4):
        try:
            with urlopen(request, timeout=900) as response:
                return json.load(response).get("elements", [])
        except HTTPError as error:
            # 429 and 504 mean the server is busy; wait for a free slot and ask again
            if error.code not in (429, 504) or attempt == 3:
                raise RuntimeError(f"Overpass answered {error.code}; it may be busy, try again later") from error
        except (URLError, TimeoutError) as error:
            if attempt == 3:
                raise RuntimeError(f"Could not reach Overpass: {error}") from error
        time.sleep(30 * (attempt + 1))
    return []


def download(overpass_url: str) -> dict:
    """Every drivable way in Singapore and its nodes, as one Overpass-style answer."""
    elements: dict[tuple[str, int], dict] = {}
    height = (NORTH - SOUTH) / GRID_ROWS
    width = (EAST - WEST) / GRID_COLUMNS
    for row in range(GRID_ROWS):
        for column in range(GRID_COLUMNS):
            box = (SOUTH + row * height, WEST + column * width,
                   SOUTH + (row + 1) * height, WEST + (column + 1) * width)
            piece = _download_piece(overpass_url, box)
            # A way crossing a piece edge comes back from both pieces, with all its nodes each time
            for element in piece:
                elements[(element["type"], int(element["id"]))] = element
            print(f"  piece {row * GRID_COLUMNS + column + 1} of {GRID_ROWS * GRID_COLUMNS}: {len(piece)} elements")
    return {"elements": list(elements.values())}


def write_database(path: Path, osm: dict, transit_path: Path | None) -> dict[str, int]:
    """Write the road database from an Overpass JSON answer, replacing any older file at once."""
    nodes = {}
    ways = []
    for element in osm.get("elements", []):
        if element.get("type") == "node":
            nodes[int(element["id"])] = (float(element["lat"]), float(element["lon"]))
        elif element.get("type") == "way":
            ways.append(element)
    path.parent.mkdir(parents=True, exist_ok=True)
    partial = path.with_name(path.name + ".partial")
    partial.unlink(missing_ok=True)
    connection = sqlite3.connect(partial)
    used = set()
    road_count = 0
    try:
        connection.executescript(SCHEMA)
        for way in ways:
            references = [int(node) for node in way.get("nodes", []) if int(node) in nodes]
            if len(references) < 2:
                continue
            tags = way.get("tags", {})
            connection.execute(
                f"INSERT INTO roads VALUES (?, ?{', ?' * len(TAG_COLUMNS)})",
                (int(way["id"]), tags["highway"], *(tags.get(column) for column in TAG_COLUMNS)))
            connection.executemany("INSERT INTO road_nodes VALUES (?, ?, ?)",
                                   [(int(way["id"]), node, index) for index, node in enumerate(references)])
            used.update(references)
            road_count += 1
        connection.executemany("INSERT INTO nodes VALUES (?, ?, ?)",
                               [(node, *nodes[node]) for node in sorted(used)])
        connection.execute("INSERT INTO nodes_rtree SELECT node_id, lon, lon, lat, lat FROM nodes")
        stop_count = 0
        if transit_path is not None and transit_path.is_file():
            connection.execute("ATTACH DATABASE ? AS transit", (str(transit_path),))
            # LTA stop codes are five digits, so they fit the integer key unchanged
            stop_count = connection.execute(
                "INSERT INTO bus_stops SELECT CAST(code AS INTEGER), description, lat, lon FROM transit.bus_stops"
                " WHERE code GLOB '[0-9]*'").rowcount
            connection.commit()
            connection.execute("DETACH DATABASE transit")
        connection.commit()
    finally:
        connection.close()
    partial.replace(path)
    return {"roads": road_count, "nodes": len(used), "bus_stops": stop_count}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    project_root = Path(__file__).resolve().parents[3]
    parser.add_argument("--output", type=Path, default=project_root / "state" / "singapore" / "map.db")
    parser.add_argument("--overpass", default=OVERPASS_URL, help="Overpass interpreter URL")
    parser.add_argument("--from-json", type=Path, default=None, help="Use a saved Overpass answer instead")
    arguments = parser.parse_args(argv)
    try:
        if arguments.from_json is not None:
            osm = json.loads(arguments.from_json.read_text(encoding="utf-8"))
        else:
            print("Downloading Singapore roads from Overpass (several minutes)...")
            osm = download(arguments.overpass)
    except (RuntimeError, OSError, ValueError) as error:
        print(error, file=sys.stderr)
        return 1
    counts = write_database(arguments.output, osm, get_settings().transit_database_path)
    print(f"Wrote {counts['roads']} roads, {counts['nodes']} nodes and {counts['bus_stops']} bus stops"
          f" to {arguments.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
