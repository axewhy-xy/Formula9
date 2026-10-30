"""Turn Singapore's vehicle population into the background traffic for an area's 3D view. Needs no key.

Run from backend/: python -m src.traffic.import_vehicle_population [--area toa-payoh] [--percent 0.1] [--year YYYY] [--seed N] [--offline]

The population comes from LTA's Annual Motor Vehicle Population by Vehicle Type on data.gov.sg. The
download is saved at fixtures/traffic/vehicle-population.json; --offline rebuilds from that copy.

A small share of each vehicle type is placed in a lane of the area's real roads, from the
OpenStreetMap extract at fixtures/osm/<area>.overpass.json, and written to
fixtures/traffic/<area>.json. Positions are local metres in the frame the fleet map uses (x east and
y north of central Singapore) and headings are degrees anticlockwise from east, like the backend's
vehicles. The file also holds the road network, split at junctions, so vehicles can be driven along
it. The same inputs and seed always give the same file.

Default share: the Toa Payoh box is 4.87 km2, about 0.66% of Singapore's land, so about 6,700 of the
1.0 million vehicles belong to it by area. 0.1% of the national population is about 1,000 vehicles,
which assumes about 15% of those are on the road at once. The national mix of types is kept as is:
the dataset says nothing about where vehicles are, so the area's own mix is not known.
"""

from __future__ import annotations

import argparse
import json
import math
import random
import sys
import time
from datetime import date
from pathlib import Path
from typing import Callable
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import Request, urlopen

from src.routing.build_singapore_fleet_fixture import DEFAULT_AREA, SERVICE_AREAS, extract_path, project
from src.routing.import_buildings import number

DATASET_ID = "d_2873f3b1b2a836103f51f696350b98fa"
DATASET_NAME = "Annual Motor Vehicle Population by Vehicle Type"
SEARCH_URL = "https://data.gov.sg/api/action/datastore_search"
PAGE_SIZE = 1000
ATTEMPTS = 4
OUTPUT_DIRECTORY = Path(__file__).resolve().parents[2] / "fixtures" / "traffic"
SNAPSHOT = OUTPUT_DIRECTORY / "vehicle-population.json"
ROADS_ATTRIBUTION = "Roads (c) OpenStreetMap contributors, available under the Open Database Licence"

DEFAULT_PERCENT = 0.1
DEFAULT_SEED = 9

# What the view draws for each vehicle type: a typical size, and paint in rough proportion. Both are
# illustrative; the dataset only gives counts.
MODELS = {
    "car": {"length_m": 4.5, "width_m": 1.8, "height_m": 1.5,
            "colors": ["#f2f2f2", "#f2f2f2", "#c4c7cc", "#8a8d91", "#23252a", "#23252a", "#7c1f24", "#1f3a68"]},
    "taxi": {"length_m": 4.6, "width_m": 1.8, "height_m": 1.5, "colors": ["#1c6dd0", "#1c6dd0", "#23252a", "#f2c200"]},
    "motorcycle": {"length_m": 2.1, "width_m": 0.8, "height_m": 1.4, "colors": ["#23252a", "#c4c7cc", "#b3261e", "#1f3a68"]},
    "van": {"length_m": 5.2, "width_m": 1.9, "height_m": 2.1, "colors": ["#f2f2f2", "#f2f2f2", "#c4c7cc"]},
    "truck": {"length_m": 8.5, "width_m": 2.4, "height_m": 3.2, "colors": ["#f2f2f2", "#2f6b4f", "#1f3a68"]},
    "heavy_truck": {"length_m": 16.0, "width_m": 2.5, "height_m": 3.8, "colors": ["#c4c7cc", "#b3621e", "#2f6b4f"]},
    "bus": {"length_m": 12.0, "width_m": 2.55, "height_m": 3.1, "colors": ["#7b3f98", "#5a9e2f", "#f2f2f2", "#d8232a"]},
}
# Too big for car-park lanes and the driveways between blocks
LARGE_MODELS = {"bus", "truck", "heavy_truck"}
LARGE_MODEL_BARRED_ROADS = {"service"}

ROAD_CLASSES = {"primary", "primary_link", "secondary", "secondary_link", "tertiary", "tertiary_link",
                "unclassified", "residential", "living_street", "service"}
# Where OSM gives no limit
DEFAULT_SPEED_KPH = {"primary": 60, "primary_link": 40, "secondary": 50, "secondary_link": 40, "tertiary": 50,
                     "tertiary_link": 40, "unclassified": 40, "residential": 40, "living_street": 20, "service": 20}
# How much busier a lane of each road is than a residential one. An assumption: no traffic counts are used.
ROAD_WEIGHT = {"primary": 8, "primary_link": 4, "secondary": 5, "secondary_link": 3, "tertiary": 3, "tertiary_link": 2,
               "unclassified": 1, "residential": 1, "living_street": 0.5, "service": 0.5}

LANE_WIDTH_M = 3.2
# A lane is cut into slots of one vehicle each, so no two vehicles overlap. A slot fits the longest
# model with a gap, and slots keep back from junctions, where roads cross.
SLOT_M = 20.0
MINIMUM_GAP_M = 2.0
JUNCTION_CLEARANCE_M = 10.0
# Keeps a vehicle's lane offset and its place in its slot inside the area
BOX_INSET_M = 15.0


def model_for(category: str, vehicle_type: str) -> str:
    """The model drawn for a row of the dataset. Unknown types fail, so a new one is never silently dropped."""
    kind = vehicle_type.lower()
    if "taxi" in category.lower():
        return "taxi"
    for words, model in (("motorcycle", "motorcycle"), ("bus", "bus"), ("very heavy", "heavy_truck"), ("heavy goods", "truck"),
                         ("light goods", "van"), ("goods-cum", "van"), ("goods", "truck"), ("car", "car")):
        if words in kind:
            return model
    raise ValueError(f"No vehicle model for {category!r} / {vehicle_type!r}")


def fetch_page(offset: int) -> dict:
    query = urlencode({"resource_id": DATASET_ID, "limit": PAGE_SIZE, "offset": offset})
    request = Request(f"{SEARCH_URL}?{query}", headers={"User-Agent": "Formula9-traffic/1.0"})
    for attempt in range(ATTEMPTS):
        try:
            with urlopen(request, timeout=60) as response:
                return json.load(response)["result"]
        except HTTPError as error:
            # data.gov.sg limits how often it may be called
            if error.code != 429 or attempt == ATTEMPTS - 1:
                raise
            time.sleep(2 ** (attempt + 1))
    raise AssertionError("unreachable")


def download_rows(fetch: Callable[[int], dict] = fetch_page) -> list[dict]:
    """Every row of the dataset, page by page."""
    rows: list[dict] = []
    while True:
        page = fetch(len(rows))
        rows += page["records"]
        if len(rows) >= page["total"]:
            return rows
        if not page["records"]:
            raise ValueError(f"The dataset stopped at {len(rows)} of {page['total']} rows")


def population_of_year(rows: list[dict], year: int | None) -> tuple[int, list[dict]]:
    """The chosen year's types, or the latest year's, as (year, types)."""
    chosen = year if year is not None else max(int(row["year"]) for row in rows)
    types = [{"category": row["category"], "type": row["type"], "population": int(row["number"]),
              "model": model_for(row["category"], row["type"])}
             for row in rows if int(row["year"]) == chosen]
    if not types:
        raise ValueError(f"The dataset has no rows for {chosen}")
    return chosen, types


def sample_size(population: int, percent: float) -> int:
    """How many to show of a type: its share, but at least one of any type that exists at all."""
    return max(1, round(population * percent / 100)) if population > 0 else 0


def road_network(osm: dict, box: tuple[float, float, float, float]) -> list[dict]:
    """The area's drivable roads, split at junctions. Points run in the direction of travel if one-way."""
    elements = osm["elements"]
    position = {e["id"]: project(e["lat"], e["lon"]) for e in elements if e["type"] == "node"}
    ways = []
    for way in (e for e in elements if e["type"] == "way"):
        tags = way.get("tags", {})
        if (tags.get("highway") not in ROAD_CLASSES or tags.get("access") in ("private", "no")
                or tags.get("motor_vehicle") == "no"):
            continue
        nodes = [n for n in way["nodes"] if n in position]
        if tags.get("oneway") == "-1":
            nodes.reverse()
        ways.append((way["id"], tags, nodes))
    uses: dict[int, int] = {}
    for _, _, nodes in ways:
        for node in nodes:
            uses[node] = uses.get(node, 0) + 1
    west, south, east, north = area_box_m(box)
    roads = []
    for way_id, tags, nodes in sorted(ways, key=lambda way: way[0]):
        oneway = tags.get("oneway") in ("yes", "true", "1", "-1") or tags.get("junction") in ("roundabout", "circular")
        lanes = int(number(tags.get("lanes")) or 0)
        start = 0
        section = 0
        for index in range(1, len(nodes)):
            if index < len(nodes) - 1 and uses[nodes[index]] < 2:
                continue
            piece = nodes[start:index + 1]
            start = index
            section += 1
            points = [position[n] for n in piece]
            if not any(west <= x <= east and south <= y <= north for x, y in points):
                continue
            roads.append({"id": f"way/{way_id}/{section}", "name": tags.get("name", ""), "class": tags["highway"],
                          "from_node": piece[0], "to_node": piece[-1], "oneway": oneway,
                          "lanes": max(1, lanes if oneway else lanes // 2),
                          "speed_kph": int(number(tags.get("maxspeed")) or DEFAULT_SPEED_KPH[tags["highway"]]),
                          "points_m": [list(point) for point in points]})
    return roads


def area_box_m(box: tuple[float, float, float, float]) -> tuple[float, float, float, float]:
    """An area's (south, west, north, east) box as local (west, south, east, north) metres."""
    west, south = project(box[0], box[1])
    east, north = project(box[2], box[3])
    return west, south, east, north


def point_along(points: list[list[float]], distance: float, left_m: float) -> tuple[float, float, float]:
    """(x, y, heading) at a distance along a line, moved sideways to the left of travel."""
    for (ax, ay), (bx, by) in zip(points, points[1:]):
        length = math.dist((ax, ay), (bx, by))
        if distance <= length and length > 0:
            ux, uy = (bx - ax) / length, (by - ay) / length
            return (round(ax + ux * distance - uy * left_m, 2), round(ay + uy * distance + ux * left_m, 2),
                    round(math.degrees(math.atan2(uy, ux)) % 360, 1))
        distance -= length
    raise ValueError("The distance is past the end of the line")


def lane_slots(roads: list[dict], box: tuple[float, float, float, float]) -> list[dict]:
    """Every place inside the area that one vehicle can be put."""
    west, south, east, north = area_box_m(box)
    slots = []
    for road in roads:
        points = road["points_m"]
        length = sum(math.dist(a, b) for a, b in zip(points, points[1:]))
        count = int((length - 2 * JUNCTION_CLEARANCE_M) // SLOT_M)
        if count < 1:
            continue
        spacing = (length - 2 * JUNCTION_CLEARANCE_M) / count
        for direction in (1,) if road["oneway"] else (1, -1):
            path = points if direction == 1 else points[::-1]
            for lane in range(road["lanes"]):
                # Singapore drives on the left. Lane 0 is the rightmost lane, nearest the middle of the road.
                left_m = (lane - (road["lanes"] - 1) / 2) * LANE_WIDTH_M if road["oneway"] else (lane + 0.5) * LANE_WIDTH_M
                for index in range(count):
                    distance = JUNCTION_CLEARANCE_M + spacing * (index + 0.5)
                    x, y, _ = point_along(path, distance, left_m)
                    if west + BOX_INSET_M <= x <= east - BOX_INSET_M and south + BOX_INSET_M <= y <= north - BOX_INSET_M:
                        slots.append({"road": road, "path": path, "direction": direction, "lane": lane, "left_m": left_m,
                                      "distance": distance})
    return slots


def vehicle_in_slot(model: str, row: dict, slot: dict, rng: random.Random) -> dict:
    free = max(0.0, SLOT_M - MODELS[model]["length_m"] - MINIMUM_GAP_M)
    distance = slot["distance"] + rng.uniform(-free / 2, free / 2)
    x, y, heading = point_along(slot["path"], distance, slot["left_m"])
    road = slot["road"]
    return {"model": model, "category": row["category"], "type": row["type"],
            "color": rng.choice(MODELS[model]["colors"]), "road": road["id"], "direction": slot["direction"],
            "lane": slot["lane"], "lane_offset_m": round(slot["left_m"], 2), "distance_m": round(distance, 2),
            "x_m": x, "y_m": y, "heading_deg": heading, "speed_kph": road["speed_kph"]}


def grid_cell(vehicle: dict) -> tuple[int, int]:
    return int(vehicle["x_m"] // SLOT_M), int(vehicle["y_m"] // SLOT_M)


def clear_of_other_roads(vehicle: dict, near: dict[tuple[int, int], list[dict]]) -> bool:
    """Whether it keeps off vehicles on other sections. Slots only keep a lane clear, and a slip road
    can run alongside the road it joins for longer than the clearance at a junction."""
    cell_x, cell_y = grid_cell(vehicle)
    for x in (cell_x - 1, cell_x, cell_x + 1):
        for y in (cell_y - 1, cell_y, cell_y + 1):
            for other in near.get((x, y), ()):
                reach = (MODELS[vehicle["model"]]["length_m"] + MODELS[other["model"]]["length_m"]) / 2 + MINIMUM_GAP_M
                if other["road"] != vehicle["road"] and math.dist((vehicle["x_m"], vehicle["y_m"]), (other["x_m"], other["y_m"])) < reach:
                    return False
    return True


def place_vehicles(types: list[dict], roads: list[dict], box: tuple[float, float, float, float], rng: random.Random) -> list[dict]:
    """Put each type's vehicles into free slots, favouring the busier roads."""
    slots = lane_slots(roads, box)
    # Weighted sampling without replacement (Efraimidis and Spirakis): slots in the order they are used
    ranked = sorted(slots, key=lambda slot: rng.random() ** (1 / ROAD_WEIGHT[slot["road"]["class"]]), reverse=True)
    wanted = [(row["model"], row) for row in types for _ in range(row["count"])]
    large = [item for item in wanted if item[0] in LARGE_MODELS]
    small = [item for item in wanted if item[0] not in LARGE_MODELS]
    rng.shuffle(large)
    rng.shuffle(small)
    large_slots = iter([slot for slot in ranked if slot["road"]["class"] not in LARGE_MODEL_BARRED_ROADS])
    small_slots = iter(ranked)

    placed = []
    taken = set()
    near: dict[tuple[int, int], list[dict]] = {}
    for model, row in large + small:
        for slot in large_slots if model in LARGE_MODELS else small_slots:
            if id(slot) in taken:
                continue
            vehicle = vehicle_in_slot(model, row, slot, rng)
            if clear_of_other_roads(vehicle, near):
                break
        else:
            raise ValueError(f"{len(wanted)} vehicles do not fit on the {len(slots)} free places on these roads; use a smaller --percent")
        taken.add(id(slot))
        placed.append(vehicle)
        near.setdefault(grid_cell(vehicle), []).append(vehicle)
    placed.sort(key=lambda v: (v["model"], v["category"], v["type"], v["road"], v["direction"], v["lane"], v["distance_m"]))
    numbers: dict[str, int] = {}
    for vehicle in placed:
        numbers[vehicle["model"]] = numbers.get(vehicle["model"], 0) + 1
        vehicle["id"] = f"{vehicle['model']}-{numbers[vehicle['model']]:04d}"
    return placed


def build(rows: list[dict], osm: dict, area_key: str, percent: float, year: int | None, seed: int, retrieved: str) -> dict:
    if percent <= 0:
        raise ValueError("--percent must be above 0")
    area = SERVICE_AREAS[area_key]
    chosen, types = population_of_year(rows, year)
    for row in types:
        row["count"] = sample_size(row["population"], percent)
    roads = road_network(osm, area["box"])
    vehicles = place_vehicles(types, roads, area["box"], random.Random(seed))
    west, south, east, north = area_box_m(area["box"])
    return {
        "attribution": [f"Contains information from {DATASET_NAME} accessed on {retrieved} from data.gov.sg, which is made "
                        "available under the terms of the Singapore Open Data Licence version 1.0", ROADS_ATTRIBUTION],
        "dataset": {"id": DATASET_ID, "name": DATASET_NAME, "year": chosen, "retrieved": retrieved},
        "area": {"key": area_key, "name": area["name"], "box_latlon": list(area["box"]), "box_m": [west, south, east, north]},
        "sampling": {"percent": percent, "seed": seed, "national_population": sum(row["population"] for row in types),
                     "vehicle_count": len(vehicles)},
        "models": {name: {key: value for key, value in model.items() if key != "colors"} for name, model in MODELS.items()},
        "types": types,
        "vehicles": vehicles,
        "roads": roads,
    }


def write_json(path: Path, data: dict) -> None:
    """Compact, but one list item per line, so a rebuild's diff shows what changed."""
    parts = []
    for key, value in data.items():
        if isinstance(value, list) and value and isinstance(value[0], dict):
            items = ",\n".join("  " + json.dumps(item, separators=(",", ":")) for item in value)
            parts.append(f"{json.dumps(key)}: [\n{items}\n ]")
        else:
            parts.append(f"{json.dumps(key)}: {json.dumps(value, separators=(',', ':'))}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("{\n " + ",\n ".join(parts) + "\n}\n", encoding="utf-8", newline="\n")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--area", choices=sorted(SERVICE_AREAS), default=DEFAULT_AREA)
    parser.add_argument("--percent", type=float, default=DEFAULT_PERCENT, help="share of each vehicle type to show (default %(default)s)")
    parser.add_argument("--year", type=int, default=None, help="year of the dataset to use (default: the latest)")
    parser.add_argument("--seed", type=int, default=DEFAULT_SEED, help="choice of where vehicles go and how they look")
    parser.add_argument("--offline", action="store_true", help=f"use the saved {SNAPSHOT.name} instead of downloading")
    parser.add_argument("--output", type=Path, default=None, help="JSON file to write")
    arguments = parser.parse_args(argv)
    try:
        if arguments.offline:
            snapshot = json.loads(SNAPSHOT.read_text(encoding="utf-8"))
        else:
            snapshot = {"retrieved": date.today().isoformat(), "rows": download_rows()}
            write_json(SNAPSHOT, snapshot)
        osm = json.loads(extract_path(arguments.area).read_text(encoding="utf-8"))
        data = build(snapshot["rows"], osm, arguments.area, arguments.percent, arguments.year, arguments.seed, snapshot["retrieved"])
    except (OSError, KeyError, ValueError) as error:
        print(f"Could not prepare the traffic: {error}", file=sys.stderr)
        return 1
    output = arguments.output or OUTPUT_DIRECTORY / f"{arguments.area}.json"
    write_json(output, data)
    print(f"{data['area']['name']}: {arguments.percent}% of the {data['dataset']['year']} vehicle population")
    for row in data["types"]:
        print(f"  {row['count']:>4} of {row['population']:>7}  {row['category']} / {row['type']} ({row['model']})")
    print(f"Wrote {len(data['vehicles'])} vehicles on {len(data['roads'])} road sections to {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
