"""Turn LTA's traffic light, crossing and road marking data into JSON an area's views can draw. Needs no key.

Run from backend/: python -m src.traffic.import_road_features [--area toa-payoh] [--release Sep2026] [--source-dir DIR]

Downloads DataMall's static geospatial files, keeps what is in the area and writes signals, crossings
and markings JSON to fixtures/traffic/. Positions are local metres in the frame the fleet map uses (x
east and y north of central Singapore). --source-dir reads zips already downloaded to a folder.

From LTA: every position, bearing, road code and type code. LTA does not say what its type codes mean,
so they are passed on unexplained. LTA's staff usernames and job numbers are left out.
Worked out here, and marked as such in the files: which heads form a junction, the ways into it from the
OpenStreetMap roads, the approach, direction and phase of each head, whether a crossing has a signal, and
each crossing's stripes or edges. See src.traffic.signal_cycle for the colours, which are simulated.
"""

from __future__ import annotations

import argparse
import json
import math
import sys
import tempfile
import zipfile
from datetime import date
from pathlib import Path
from urllib.request import Request, urlopen

from src.routing.build_singapore_fleet_fixture import DEFAULT_AREA, SERVICE_AREAS, extract_path, project
from src.traffic import signal_cycle
from src.traffic.import_vehicle_population import OUTPUT_DIRECTORY, area_box_m, point_along, road_network, write_json

DOWNLOAD_URL = "https://datamall.lta.gov.sg/content/dam/datamall/datasets/Geospatial/{name}_{release}.zip"
LICENCE_URL = "https://datamall.lta.gov.sg/content/datamall/en/SingaporeOpenDataLicence.html"
DEFAULT_RELEASE = "Sep2026"
LAYERS = ("TrafficLight", "RoadCrossing", "LaneMarking", "ArrowMarking", "WordMarking", "RoadHump")

# Heads closer than this are one junction. The junction count barely moves from 50 to 70 m.
JUNCTION_LINK_M = 60.0
# A crossing this close to a head counts as signalised. In Toa Payoh the crossings split cleanly: no
# crossing's nearest head is between 11.5 and 37.9 m away.
SIGNAL_REACH_M = 15.0
# Heads this far outside the area still count, so a crossing on its edge is not taken for unsignalised
SIGNAL_MARGIN_DEGREES = 0.0005
# A junction's approaches all within this many degrees of each other are on one road, with nothing to take turns with
SAME_AXIS_DEGREES = 30.0
# A road section ending this close to a head leads into its junction
APPROACH_REACH_M = 25.0
# A head controls an approach within this distance of it, counting the penalty for being on the far side
HEAD_MATCH_M = 40.0
WRONG_SIDE_PENALTY_M = 6.0
# How much of the end of an approach is compared with heads, sets its heading, and is left clear past its stop point
APPROACH_STRETCH_M = 40.0
HEADING_STRETCH_M = 15.0
STOP_BACK_M = 8.0
# Sizes a view can draw crossings at. Chosen to look right, as LTA only gives the walking line.
CROSSING_WIDTH_M = 3.0
STRIPE_THICKNESS_M = 0.5
STRIPE_GAP_M = 0.5


def attribution(release: str, retrieved: str) -> list[str]:
    return [f"Contains information from LTA DataMall static datasets ({release}) accessed on {retrieved}, which is made "
            f"available under the terms of the Singapore Open Data Licence version 1.0: {LICENCE_URL}"]


def read_layer(path: Path, box: tuple[float, float, float, float]) -> list[dict]:
    """A zip's rows touching a (south, west, north, east) box. Multi-part shapes become one row per part."""
    import geopandas  # Only the import needs it, not the backend serving the result

    with zipfile.ZipFile(path) as archive:
        inner = next(name for name in archive.namelist() if name.endswith(".shp"))
    frame = geopandas.read_file(f"zip://{path.as_posix()}!{inner}")
    # Some of LTA's rows have no shape at all
    frame = frame[frame.geometry.notna() & ~frame.geometry.is_empty].to_crs(4326)
    south, west, north, east = box
    frame = frame.cx[west:east, south:north].explode(index_parts=False)
    rows = []
    for row in frame.to_dict("records"):
        points = [(y, x) for x, y, *_ in row["geometry"].coords]
        rows.append({"points": points, "type_code": text(row.get("TYP_CD")), "road_code": text(row.get("RD_CD")),
                     "word": text(row.get("DESC_TXT")), "bearing_deg": row.get("BEARG_NUM"), "level": row.get("LVL_NUM")})
    return rows


def text(value) -> str:
    return value.strip() if isinstance(value, str) else ""


def metres(row: dict) -> list[list[float]]:
    return [list(project(*point)) for point in row["points"]]


def sorted_rows(rows: list[dict]) -> list[dict]:
    """In a repeatable order, as a shapefile's order may change between releases."""
    return sorted(rows, key=lambda row: (row["points"], row.get("type_code", ""), row.get("bearing_deg") or 0))


def group_junctions(positions: list[tuple[float, float]]) -> list[list[int]]:
    """Indexes of the heads forming each junction: heads linked by a chain of near neighbours."""
    parent = list(range(len(positions)))

    def root(index: int) -> int:
        while parent[index] != index:
            parent[index] = parent[parent[index]]
            index = parent[index]
        return index

    for a in range(len(positions)):
        for b in range(a + 1, len(positions)):
            if math.dist(positions[a], positions[b]) <= JUNCTION_LINK_M:
                parent[root(a)] = root(b)
    groups: dict[int, list[int]] = {}
    for index in range(len(positions)):
        groups.setdefault(root(index), []).append(index)
    return list(groups.values())


def phases_of(bearings: list[float]) -> list[int]:
    """Split heads into two phases by road axis, so opposite approaches go together and cross ones do not.

    An axis ignores which way along the road something points, so opposite directions match. The two
    widest gaps around the circle of axes separate the two roads. If every axis is close together there
    is only one road, as at a crossing between junctions, and all its heads share a phase. The busier
    road is the first phase.
    """
    if not bearings:
        return []
    order = sorted(range(len(bearings)), key=lambda index: bearings[index] % 180)
    axes = [bearings[index] % 180 for index in order]
    gaps = [(following - axis, position) for position, (axis, following) in enumerate(zip(axes, axes[1:] + [axes[0] + 180]))]
    wide = sorted(gaps, reverse=True)[:2]
    phases = [0] * len(bearings)
    if wide[0][0] >= 180 - SAME_AXIS_DEGREES:
        return phases
    first, last = sorted(position for _, position in wide)
    for index in order[first + 1:last + 1]:
        phases[index] = 1
    return [1 - phase for phase in phases] if sum(phases) * 2 > len(phases) else phases


def heading_of_bearing(bearing: float) -> float:
    """LTA's bearing, clockwise from north, as a heading like the vehicles', anticlockwise from east."""
    return round((90 - bearing) % 360, 1)


def line_length(line: list[list[float]]) -> float:
    return sum(math.dist(a, b) for a, b in zip(line, line[1:]))


def last_stretch(path: list[list[float]], metres_wanted: float) -> list[list[float]]:
    """The end of a line: its final metres, in the same order."""
    stretch = [path[-1]]
    left = metres_wanted
    for a, b in zip(path[-2::-1], path[:0:-1]):
        length = math.dist(a, b)
        if length >= left:
            share = left / length
            stretch.append([b[0] + (a[0] - b[0]) * share, b[1] + (a[1] - b[1]) * share])
            break
        stretch.append(a)
        left -= length
    return stretch[::-1]


def directions_of(road: dict) -> list[list[list[float]]]:
    """A road's points in each direction it can be driven."""
    return [road["points_m"]] if road["oneway"] else [road["points_m"], road["points_m"][::-1]]


def approach_on(road: dict, path: list[list[float]]) -> dict:
    """One way into a junction: where traffic on this path heads, and where it stops."""
    stretch = last_stretch(path, HEADING_STRETCH_M)
    x, y, _ = point_along(path, max(0.0, line_length(path) - STOP_BACK_M), 0.0)
    heading = math.degrees(math.atan2(stretch[-1][1] - stretch[0][1], stretch[-1][0] - stretch[0][0])) % 360
    return {"road": road["id"], "road_name": road["name"], "lanes": road["lanes"], "heading_deg": round(heading, 1),
            "stop_x_m": x, "stop_y_m": y, "_stretch": last_stretch(path, APPROACH_STRETCH_M)}


def closest_cut(path: list[list[float]], point: tuple[float, float]) -> list[list[float]]:
    """A line up to the place nearest a point."""
    index = min(range(len(path) - 1), key=lambda i: distance_to_segment(point, path[i], path[i + 1]))
    a, b = path[index], path[index + 1]
    dx, dy = b[0] - a[0], b[1] - a[1]
    length_squared = dx * dx + dy * dy
    share = 0.0 if length_squared == 0 else max(0.0, min(1.0, ((point[0] - a[0]) * dx + (point[1] - a[1]) * dy) / length_squared))
    return path[:index + 1] + [[round(a[0] + share * dx, 2), round(a[1] + share * dy, 2)]]


def approaches_of(positions: list[tuple[float, float]], roads: list[dict]) -> list[dict]:
    """The ways into a group of heads, from the road map.

    A road section ending by the heads leads in, unless it also starts by them, which is a link inside
    the junction. Signals between junctions stand on a road that runs straight past, with no section
    ending there, so that road's directions are used instead, up to the signal.
    """
    def by_heads(point: list[float]) -> bool:
        return any(math.dist(point, head) <= APPROACH_REACH_M for head in positions)

    approaches = [approach_on(road, path) for road in roads for path in directions_of(road)
                  if by_heads(path[-1]) and not by_heads(path[0])]
    if approaches or not roads:
        return approaches
    center = (sum(p[0] for p in positions) / len(positions), sum(p[1] for p in positions) / len(positions))
    distance, _, road = min(((min(distance_to_segment(center, a, b) for a, b in zip(r["points_m"], r["points_m"][1:])), i, r)
                             for i, r in enumerate(roads)), key=lambda item: item[:2])
    if distance > APPROACH_REACH_M:
        return []
    # A cut too near the road's start has nothing to measure a heading along
    cut = (closest_cut(path, center) for path in directions_of(road))
    return [approach_on(road, path) for path in cut if line_length(path) > 1]


def approach_for_head(head: tuple[float, float], approaches: list[dict]) -> dict | None:
    """The approach a head most likely controls: the nearest, preferring one with the head on its left, the kerb side."""
    def score(approach: dict) -> float:
        stretch = approach["_stretch"]
        index = min(range(len(stretch) - 1), key=lambda i: distance_to_segment(head, stretch[i], stretch[i + 1]))
        a, b = stretch[index], stretch[index + 1]
        left = (b[0] - a[0]) * (head[1] - a[1]) - (b[1] - a[1]) * (head[0] - a[0]) > 0
        return distance_to_segment(head, a, b) + (0 if left else WRONG_SIDE_PENALTY_M)

    best = min(approaches, key=score, default=None)
    return best if best is not None and score(best) <= HEAD_MATCH_M else None


def build_signals(rows: list[dict], box: tuple[float, float, float, float], roads: list[dict]) -> dict:
    west, south, east, north = area_box_m(box)
    heads = [row for row in sorted_rows(rows)
             if west <= project(*row["points"][0])[0] <= east and south <= project(*row["points"][0])[1] <= north]
    positions = [project(*head["points"][0]) for head in heads]
    groups = group_junctions(positions)
    groups.sort(key=lambda group: min(positions[index] for index in group))

    junctions = []
    signals = []
    for number, group in enumerate(groups, 1):
        junction_id = f"junction-{number:03d}"
        center = [round(sum(positions[index][axis] for index in group) / len(group), 2) for axis in (0, 1)]
        approaches = sorted(approaches_of([positions[index] for index in group], roads), key=lambda a: (a["road"], a["heading_deg"]))
        for count, (approach, phase) in enumerate(zip(approaches, phases_of([a["heading_deg"] for a in approaches])), 1):
            approach.update(id=f"{junction_id}-approach-{count}", phase=phase)
        lta_phases = phases_of([float(heads[index]["bearing_deg"]) for index in group])
        for index, lta_phase in zip(group, lta_phases):
            head = heads[index]
            bearing = float(head["bearing_deg"])
            controlled = approach_for_head(positions[index], approaches)
            # Off the road map, as on an expressway, only LTA's own bearing says anything about direction
            traffic = controlled["heading_deg"] if controlled else heading_of_bearing(bearing)
            signals.append({"junction": junction_id, "phase": controlled["phase"] if controlled else lta_phase,
                            "approach": controlled["id"] if controlled else None,
                            "traffic_heading_deg": traffic, "facing_heading_deg": round((traffic + 180) % 360, 1),
                            "direction_source": "road_map" if controlled else "lta_bearing",
                            "type_code": head["type_code"], "bearing_deg": round(bearing, 1), "road_code": head["road_code"],
                            "x_m": positions[index][0], "y_m": positions[index][1]})
        junctions.append({"id": junction_id, "x_m": center[0], "y_m": center[1], "head_count": len(group),
                          "offset_s": signal_cycle.junction_offset(junction_id),
                          "approaches": [{key: value for key, value in approach.items() if not key.startswith("_")} for approach in approaches]})
    signals.sort(key=lambda signal: (signal["junction"], signal["x_m"], signal["y_m"], signal["bearing_deg"]))
    for number, signal in enumerate(signals, 1):
        signal["id"] = f"signal-{number:04d}"
    return {"junctions": junctions, "signals": signals}


def distance_to_segment(point: tuple[float, float], a: list[float], b: list[float]) -> float:
    dx, dy = b[0] - a[0], b[1] - a[1]
    length_squared = dx * dx + dy * dy
    share = 0.0 if length_squared == 0 else max(0.0, min(1.0, ((point[0] - a[0]) * dx + (point[1] - a[1]) * dy) / length_squared))
    return math.dist(point, (a[0] + share * dx, a[1] + share * dy))


def crossing_shape(line: list[list[float]]) -> tuple[list[float], list[float], float]:
    """Where a crossing starts, its unit direction across the road, and its length."""
    (ax, ay), (bx, by) = line[0], line[-1]
    length = math.dist(line[0], line[-1])
    return [ax, ay], [(bx - ax) / length, (by - ay) / length], length


def zebra_stripes(line: list[list[float]]) -> list[list[list[float]]]:
    """A zebra's white bars as four corners each. Each runs along the road, and they step across it."""
    (ax, ay), (ux, uy), length = crossing_shape(line)
    count = int((length + STRIPE_GAP_M) // (STRIPE_THICKNESS_M + STRIPE_GAP_M))
    start = (length - count * STRIPE_THICKNESS_M - (count - 1) * STRIPE_GAP_M) / 2
    half = CROSSING_WIDTH_M / 2
    stripes = []
    for index in range(count):
        near = start + index * (STRIPE_THICKNESS_M + STRIPE_GAP_M)
        far = near + STRIPE_THICKNESS_M
        stripes.append([[round(ax + ux * along + -uy * side, 2), round(ay + uy * along + ux * side, 2)]
                        for along, side in ((near, -half), (far, -half), (far, half), (near, half))])
    return stripes


def crossing_edges(line: list[list[float]]) -> list[list[list[float]]]:
    """The two lines bounding a signal crossing, each side of its walking line."""
    (ax, ay), (ux, uy), length = crossing_shape(line)
    half = CROSSING_WIDTH_M / 2
    return [[[round(ax - uy * side, 2), round(ay + ux * side, 2)], [round(ax + ux * length - uy * side, 2), round(ay + uy * length + ux * side, 2)]]
            for side in (-half, half)]


def build_crossings(rows: list[dict], signal_rows: list[dict]) -> dict:
    """Each of LTA's lines is one crossing's walking line: nine in ten run across the road they are on."""
    heads = [project(*row["points"][0]) for row in signal_rows]
    crossings = []
    for number, row in enumerate(sorted_rows(rows), 1):
        line = metres(row)
        nearest = min((distance_to_segment(head, a, b) for head in heads for a, b in zip(line, line[1:])), default=math.inf)
        signalised = nearest <= SIGNAL_REACH_M
        crossing = {"id": f"crossing-{number:03d}", "line_m": line, "length_m": round(line_length(line), 2),
                    "signal_distance_m": None if nearest == math.inf else round(nearest, 1),
                    "control": "signal" if signalised else "no_signal",
                    "render_as": "signal_crossing" if signalised else "zebra"}
        if signalised:
            crossing["edges_m"] = crossing_edges(line)
        else:
            crossing["stripes_m"] = zebra_stripes(line)
        crossings.append(crossing)
    return {"crossing_shape": {"assumed": True, "note": "LTA gives only the walking line. These sizes are chosen, not measured.",
                               "width_m": CROSSING_WIDTH_M, "stripe_thickness_m": STRIPE_THICKNESS_M, "stripe_gap_m": STRIPE_GAP_M},
            "crossings": crossings}


def build_markings(lanes: list[dict], arrows: list[dict], words: list[dict], humps: list[dict]) -> dict:
    def point(row: dict, prefix: str, number: int, **fields) -> dict:
        x, y = project(*row["points"][0])
        bearing = float(row["bearing_deg"] or 0)
        return {"id": f"{prefix}-{number:05d}", **fields, "bearing_deg": round(bearing, 1), "heading_deg": heading_of_bearing(bearing),
                "level": row["level"], "road_code": row["road_code"], "x_m": x, "y_m": y}

    return {
        "lane_lines": [{"id": f"lane-{number:05d}", "type_code": row["type_code"], "level": row["level"],
                        "road_code": row["road_code"], "points_m": metres(row)}
                       for number, row in enumerate(sorted_rows(lanes), 1)],
        "arrows": [point(row, "arrow", number, type_code=row["type_code"]) for number, row in enumerate(sorted_rows(arrows), 1)],
        "words": [point(row, "word", number, text=row["word"]) for number, row in enumerate(sorted_rows(words), 1)],
        "road_humps": [{"id": f"hump-{number:04d}", "points_m": metres(row)} for number, row in enumerate(sorted_rows(humps), 1)],
    }


def build(layers: dict[str, list[dict]], signal_rows: list[dict], roads: list[dict], area_key: str, release: str, retrieved: str) -> dict[str, dict]:
    """Each file's contents, by name. signal_rows reaches past the area, so edge crossings find their heads."""
    area = SERVICE_AREAS[area_key]
    west, south, east, north = area_box_m(area["box"])
    header = {"attribution": attribution(release, retrieved),
              "source": {"name": "LTA DataMall static datasets", "release": release, "retrieved": retrieved},
              "area": {"key": area_key, "name": area["name"], "box_latlon": list(area["box"]), "box_m": [west, south, east, north]}}
    timing = {"simulated": True, "note": "Made-up timings: LTA does not publish signal colours or timings. Heads, not colours, are real.",
              "phases": signal_cycle.PHASES, "green_s": signal_cycle.GREEN_S, "yellow_s": signal_cycle.YELLOW_S,
              "all_red_s": signal_cycle.ALL_RED_S, "cycle_s": signal_cycle.CYCLE_S}
    return {
        "signals": {**header, "timing": timing, **build_signals(signal_rows, area["box"], roads)},
        "crossings": {**header, **build_crossings(layers["RoadCrossing"], signal_rows)},
        "markings": {**header, **build_markings(layers["LaneMarking"], layers["ArrowMarking"], layers["WordMarking"], layers["RoadHump"])},
    }


def output_path(area: str, kind: str, directory: Path = OUTPUT_DIRECTORY) -> Path:
    return directory / f"{area}-{kind}.json"


def download(name: str, release: str, directory: Path) -> Path:
    path = directory / f"{name}_{release}.zip"
    # DataMall's file server turns away requests that do not look like a browser
    request = Request(DOWNLOAD_URL.format(name=name, release=release), headers={"User-Agent": "Mozilla/5.0"})
    with urlopen(request, timeout=300) as response:
        path.write_bytes(response.read())
    return path


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--area", choices=sorted(SERVICE_AREAS), default=DEFAULT_AREA)
    parser.add_argument("--release", default=DEFAULT_RELEASE, help="DataMall's release, in its file names (default %(default)s)")
    parser.add_argument("--source-dir", type=Path, default=None, help="folder of zips already downloaded")
    parser.add_argument("--output-dir", type=Path, default=OUTPUT_DIRECTORY)
    arguments = parser.parse_args(argv)
    box = SERVICE_AREAS[arguments.area]["box"]
    wider = (box[0] - SIGNAL_MARGIN_DEGREES, box[1] - SIGNAL_MARGIN_DEGREES, box[2] + SIGNAL_MARGIN_DEGREES, box[3] + SIGNAL_MARGIN_DEGREES)
    try:
        with tempfile.TemporaryDirectory() as temporary:
            paths = {name: (arguments.source_dir / f"{name}_{arguments.release}.zip" if arguments.source_dir
                            else download(name, arguments.release, Path(temporary))) for name in LAYERS}
            layers = {name: read_layer(path, box) for name, path in paths.items()}
            signal_rows = read_layer(paths["TrafficLight"], wider)
        roads = road_network(json.loads(extract_path(arguments.area).read_text(encoding="utf-8")), box)
        files = build(layers, signal_rows, roads, arguments.area, arguments.release, date.today().isoformat())
    except (OSError, KeyError, ValueError, StopIteration) as error:
        print(f"Could not prepare the road features; nothing was written. {error}", file=sys.stderr)
        return 1
    for kind, data in files.items():
        write_json(output_path(arguments.area, kind, arguments.output_dir), data)
    print(f"{files['signals']['area']['name']}: {len(files['signals']['signals'])} signal heads at "
          f"{len(files['signals']['junctions'])} junctions, {len(files['crossings']['crossings'])} crossings, "
          f"{len(files['markings']['lane_lines'])} lane lines, {len(files['markings']['arrows'])} arrows, "
          f"{len(files['markings']['words'])} words, {len(files['markings']['road_humps'])} humps")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
