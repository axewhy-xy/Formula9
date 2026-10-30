"""Rebuild the integrated fleet fixture (fixtures/m2_integrated.json) on real Singapore streets.

Run from backend/: python -m src.routing.build_singapore_fleet_fixture [--area KEY] [--download | --from-json PATH]

A service area is a box of Singapore around one bus interchange, listed in SERVICE_AREAS; the
shuttles run out of that interchange as LTA buses do. Its roads, bus stops and interchange come
from the OpenStreetMap extract committed at fixtures/osm/<area>.overpass.json, so no download is
needed; --download fetches a fresh one from Overpass (no key) and saves it there.

The scenario keeps its shape: shuttle-01's loop has a main road between two real junctions, two
real alternative streets around it (bypass B and C) and a return leg past real bus stops on the
left-hand kerb; two peer loops run through other real stops, well clear of it; the depot is the
area's interchange. Stops are named by their LTA bus stop codes, which OSM carries as ``ref``.

Local metres use the anchor the native 2D map uses before a scene names one (central Singapore,
local origin 0, 0), with the backend's projection, so the fleet lands on the right streets
anywhere on the island. Everything that is not geometry (incident, energy, timing) is kept from
the existing fixture.
"""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
from pathlib import Path
from urllib.parse import urlencode
from urllib.request import Request, urlopen

import networkx as nx

FIXTURE = Path(__file__).resolve().parents[2] / "fixtures" / "m2_integrated.json"
OSM_DIRECTORY = Path(__file__).resolve().parents[2] / "fixtures" / "osm"
OVERPASS_URL = "https://overpass.kumi.systems/api/interpreter"

# Where a fleet can be built: a box (south, west, north, east) around the bus interchange it runs
# from, named as in OpenStreetMap. Add an area here and run with --area --download to use it.
SERVICE_AREAS = {
    "toa-payoh": {"name": "Toa Payoh", "box": (1.326, 103.838, 1.344, 103.860),
                  "interchange": "Toa Payoh Interchange"},
}
DEFAULT_AREA = "toa-payoh"

QUERY = """
[out:json][timeout:180];
(
  way["highway"~"^(primary|primary_link|secondary|secondary_link|tertiary|tertiary_link|unclassified|residential|living_street|service)$"]
     ["service"!~"^(parking_aisle|driveway)$"]({box});
  node["highway"="bus_stop"]({box});
  nwr["amenity"="bus_station"]({box});
);
out body;
>;
out skel qt;
"""

# The anchor of MapGeo.h and the backend's metres per degree (scene_repository._project)
ORIGIN_LATITUDE, ORIGIN_LONGITUDE = 1.3521, 103.8198
METRES_PER_DEGREE_LATITUDE = 110_574.0
METRES_PER_DEGREE_LONGITUDE = 111_320.0 * math.cos(math.radians(ORIGIN_LATITUDE))

# Roads a shuttle loop may use. HDB estate roads between blocks are often tagged service in
# OSM (car-park aisles and driveways are already left out of the download).
LOOP_ROADS = {"primary", "primary_link", "secondary", "secondary_link", "tertiary", "tertiary_link",
              "unclassified", "residential", "living_street", "service"}
# A route is at most 64 waypoints (asset_loader.MAX_WAYPOINTS); leave room for the yard
MAX_LOOP_POINTS = 60
STOP_SNAP_M = 20.0
# Peer loops keep this far from shuttle-01's roads and each other: the controller separates
# shuttles by their centres (10 m) and has no lanes, so loops must not share or hug a road
CLEARANCE_M = 35.0
MIN_PEER_AREA_M2 = 8_000.0


def project(latitude: float, longitude: float) -> tuple[float, float]:
    return (round((longitude - ORIGIN_LONGITUDE) * METRES_PER_DEGREE_LONGITUDE, 2),
            round((latitude - ORIGIN_LATITUDE) * METRES_PER_DEGREE_LATITUDE, 2))


def extract_path(area: str) -> Path:
    return OSM_DIRECTORY / f"{area}.overpass.json"


def download(area: dict) -> dict:
    query = QUERY.replace("{box}", ",".join(str(value) for value in area["box"]))
    request = Request(OVERPASS_URL, data=urlencode({"data": query}).encode(),
                      headers={"User-Agent": "Formula9-training-map/1.0"})
    with urlopen(request, timeout=300) as response:
        return json.load(response)


def slug(text: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")[:60]


class Network:
    def __init__(self, osm: dict, interchange: str):
        elements = osm["elements"]
        self.position = {e["id"]: project(e["lat"], e["lon"]) for e in elements if e["type"] == "node"}
        self.loop = nx.DiGraph()
        self.access = nx.DiGraph()
        for way in (e for e in elements if e["type"] == "way" and "highway" in e.get("tags", {})):
            tags = way["tags"]
            if tags.get("access") in ("private", "no"):
                continue
            oneway = tags.get("oneway") in ("yes", "true", "1") or tags.get("junction") in ("roundabout", "circular")
            nodes = [n for n in way["nodes"] if n in self.position]
            if tags.get("oneway") == "-1":
                nodes.reverse()
                oneway = True
            graphs = [self.access]
            if tags["highway"] in LOOP_ROADS:
                graphs.append(self.loop)
            for a, b in zip(nodes, nodes[1:]):
                length = math.dist(self.position[a], self.position[b])
                for graph in graphs:
                    graph.add_edge(a, b, length=length)
                    if not oneway:
                        graph.add_edge(b, a, length=length)
        self.stops = []
        self.depot = None
        for element in elements:
            tags = element.get("tags", {})
            if element["type"] == "node" and tags.get("highway") == "bus_stop" and tags.get("ref"):
                self.stops.append({"ref": tags["ref"], "name": tags.get("name", tags["ref"]),
                                   "at": project(element["lat"], element["lon"])})
            if element["type"] == "node" and tags.get("name") == interchange:
                self.depot = project(element["lat"], element["lon"])
        if self.depot is None:
            raise ValueError(f"{interchange} is not in the downloaded area")
        # Only the part of the network a shuttle can both reach and leave: the download's edges cut
        # roads off, and car-park loops are one-way pockets
        core = max(nx.strongly_connected_components(self.loop), key=len)
        self.loop = self.loop.subgraph(core).copy()
        degree = {n: len(set(self.loop.predecessors(n)) | set(self.loop.successors(n))) for n in self.loop}
        self.junctions = {n for n, d in degree.items() if d >= 3}

    def points(self, path: list[int]) -> list[tuple[float, float]]:
        return [self.position[n] for n in path]

    def length(self, path: list[int]) -> float:
        return sum(math.dist(self.position[a], self.position[b]) for a, b in zip(path, path[1:]))


def segment_offset(point, a, b):
    """Distance along a->b of point's foot, its distance off the line, and its side (+1 left)."""
    dx, dy = b[0] - a[0], b[1] - a[1]
    length = math.hypot(dx, dy)
    if length == 0:
        return 0.0, math.dist(point, a), 0
    t = max(0.0, min(length, ((point[0] - a[0]) * dx + (point[1] - a[1]) * dy) / length))
    foot = (a[0] + dx * t / length, a[1] + dy * t / length)
    side = 1
    if dx * (point[1] - a[1]) - dy * (point[0] - a[0]) < 0:
        side = -1
    return t, math.dist(point, foot), side


def stops_along(points, stops, used):
    """Stops on the left kerb within STOP_SNAP_M of the path, in travel order, as (index, t, stop)."""
    found = []
    for stop in stops:
        if stop["ref"] in used:
            continue
        best = None
        for index, (a, b) in enumerate(zip(points, points[1:])):
            t, off, side = segment_offset(stop["at"], a, b)
            if off <= STOP_SNAP_M and side > 0 and (best is None or off < best[3]):
                best = (index, t, stop, off)
        if best is not None:
            found.append(best[:3])
    found.sort(key=lambda item: (item[0], item[1]))
    return found


def simplify(points, tolerance):
    """Douglas-Peucker; the first and last points always stay."""
    if len(points) < 3:
        return list(points)
    a, b = points[0], points[-1]
    worst, index = 0.0, 0
    for i in range(1, len(points) - 1):
        _, off, _ = segment_offset(points[i], a, b)
        if off > worst:
            worst, index = off, i
    if worst <= tolerance:
        return [a, b]
    return simplify(points[:index + 1], tolerance)[:-1] + simplify(points[index:], tolerance)


def enclosed_area(points):
    return abs(sum(a[0] * b[1] - b[0] * a[1] for a, b in zip(points, points[1:]))) / 2


def densify(points, step=10.0):
    result = []
    for a, b in zip(points, points[1:]):
        count = max(1, int(math.dist(a, b) // step))
        result.extend((a[0] + (b[0] - a[0]) * i / count, a[1] + (b[1] - a[1]) * i / count) for i in range(count))
    result.append(points[-1])
    return result


def clear_of(points, others, clearance):
    for p in densify(points):
        for q in others:
            if abs(p[0] - q[0]) < clearance and abs(p[1] - q[1]) < clearance and math.dist(p, q) < clearance:
                return False
    return True


def road_edges(path: list[int]) -> list[tuple[int, int]]:
    """The path's segments in both directions, so a blocked road is blocked both ways."""
    return [edge for a, b in zip(path, path[1:]) for edge in ((a, b), (b, a))]


def find_main_loop(net: Network):
    """The best junction pair U, J with a main road, two bypasses and a return leg past stops."""
    reverse = net.loop.reverse(copy=False)
    # OSM maps each direction of a Singapore road as its own one-way way, so a real intersection is
    # several nodes with two ways in or out each: two are enough for a main road and a way round
    near = sorted((j for j in net.loop if net.loop.in_degree(j) >= 2
                   and math.dist(net.position[j], net.depot) < 1500),
                  key=lambda j: math.dist(net.position[j], net.depot))
    best = None
    for j in near:
        upstream = nx.single_source_dijkstra_path_length(reverse, j, cutoff=450, weight="length")
        starts = [u for u, d in upstream.items() if d >= 120 and net.loop.out_degree(u) >= 2]
        for u in sorted(starts, key=upstream.get)[:12]:
            try:
                # Bypasses keep off the main road's segments (either direction) but may cross it
                # at an intersection; blocking its nodes would also cut every side street
                main = nx.dijkstra_path(net.loop, u, j, weight="length")
                main_edges = road_edges(main)
                bypass_b = nx.dijkstra_path(nx.restricted_view(net.loop, [], main_edges), u, j, weight="length")
                # C also keeps off the middle of B, so it is a different street round, though it
                # may leave U or reach J the same way as B
                middle = bypass_b[len(bypass_b) // 3:max(len(bypass_b) // 3 + 2, 2 * len(bypass_b) // 3)]
                bypass_c = nx.dijkstra_path(nx.restricted_view(net.loop, [], main_edges + road_edges(middle)),
                                            u, j, weight="length")
                # The way back keeps off the main road and off the bypasses in their own direction;
                # only shuttle-01 runs this loop, so it never meets itself coming the other way
                taken = main_edges + list(zip(bypass_b, bypass_b[1:])) + list(zip(bypass_c, bypass_c[1:]))
                back = nx.dijkstra_path(nx.restricted_view(net.loop, [], taken), j, u, weight="length")
            except (nx.NetworkXNoPath, nx.NodeNotFound):
                continue
            main_m, back_m = net.length(main), net.length(back)
            # Main roads are mapped as one-way pairs (e.g. Toa Payoh's Lorongs), so the real way
            # round a block is long
            limit = min(2600, max(4 * main_m, main_m + 2200))
            if net.length(bypass_b) > limit or net.length(bypass_c) > limit or not 250 <= back_m <= 2200:
                continue
            # Bypass C must really be another street, not B with a corner cut
            if len(set(road_edges(bypass_c)) & set(road_edges(bypass_b))) > 0.5 * (len(bypass_c) - 1):
                continue
            # Nor may C be the way back driven the other way round
            if len(set(road_edges(bypass_c)) & set(road_edges(back))) > 0.6 * (len(bypass_c) - 1):
                continue
            stops = stops_along(net.points(back), net.stops, set())
            if not stops:
                continue
            total = main_m + back_m + net.length(bypass_b) + net.length(bypass_c)
            score = min(len(stops), 3) * 1000 - total
            if best is None or score > best[0]:
                best = (score, main, bypass_b, bypass_c, back, stops)
    if best is None:
        raise ValueError("No junction pair in the area has a main road, two bypasses and a stop on the way back")
    return best[1:]


def find_peer_loops(net: Network, avoid_points, used, count=2):
    loops = []
    candidates = sorted(net.stops, key=lambda s: math.dist(s["at"], net.depot))
    for stop in candidates:
        if stop["ref"] in used or math.dist(stop["at"], net.depot) > 2000:
            continue
        # The road the stop is on, in the direction that has it on the left kerb
        best = None
        for a, b in net.loop.edges():
            t, off, side = segment_offset(stop["at"], net.position[a], net.position[b])
            if off <= STOP_SNAP_M and side > 0 and (best is None or off < best[2]):
                best = (a, b, off, t)
        if best is None:
            continue
        a, b, _, t = best
        pa, pb = net.position[a], net.position[b]
        share = t / max(math.dist(pa, pb), 1e-9)
        foot = (round(pa[0] + (pb[0] - pa[0]) * share, 2), round(pa[1] + (pb[1] - pa[1]) * share, 2))
        # The few shortest ways round from the stop's road back onto it; the very shortest is
        # often straight back down the other carriageway
        options = nx.shortest_simple_paths(nx.restricted_view(net.loop, [], [(b, a), (a, b)]), b, a, weight="length")
        ring = None
        try:
            for attempt, around in enumerate(options):
                if attempt == 8:
                    break
                candidate = [foot] + net.points(around) + [foot]
                length = sum(math.dist(p, q) for p, q in zip(candidate, candidate[1:]))
                if length > 1800:
                    break
                # Round a real block, not up one carriageway and back down the other: two shuttles
                # on the same loop would pass 10 to 15 m apart and hold each other
                if length < 450 or enclosed_area(candidate) < MIN_PEER_AREA_M2:
                    continue
                if clear_of(candidate, avoid_points, CLEARANCE_M):
                    ring = candidate
                    break
        except nx.NetworkXNoPath:
            continue
        if ring is None:
            continue
        tolerance = 2.0
        simple = simplify(ring, tolerance)
        while len(simple) > MAX_LOOP_POINTS:
            tolerance *= 1.5
            simple = simplify(ring, tolerance)
        loops.append({"stop": stop, "ring": simple, "start_node": b, "length": length})
        used.add(stop["ref"])
        avoid_points = avoid_points + densify(ring)
        if len(loops) == count:
            return loops
    raise ValueError(f"Found only {len(loops)} of {count} peer loops clear of the main loop")


def connector(net: Network, start_point, start_node):
    """Real roads from a loop's start to the interchange, ending just inside it."""
    entry = min(net.access.nodes, key=lambda n: math.dist(net.position[n], net.depot))
    path = nx.dijkstra_path(net.access, start_node, entry, weight="length")
    points = [tuple(start_point)] + net.points(path)
    inside = (float(round(net.depot[0])), float(round(net.depot[1])) + 25.0)
    points = simplify(points, 2.0) + [inside]
    return [list(p) for index, p in enumerate(points) if index == 0 or p != points[index - 1]]


def build(osm: dict, base: dict, area: dict) -> dict:
    net = Network(osm, area["interchange"])
    main, bypass_b, bypass_c, back, stops = find_main_loop(net)

    # Split the return leg at its stops, so each stop is a node of the route graph
    back_points = net.points(back)
    pieces, stop_points = [], []
    cut_index, cut_point = 0, back_points[0]
    for index, t, stop in stops[:3]:
        a, b = back_points[index], back_points[index + 1]
        share = t / max(math.dist(a, b), 1e-9)
        foot = (round(a[0] + (b[0] - a[0]) * share, 2), round(a[1] + (b[1] - a[1]) * share, 2))
        if math.dist(foot, cut_point) < 15 or math.dist(foot, back_points[-1]) < 15:
            continue
        pieces.append([cut_point] + back_points[cut_index + 1:index + 1] + [foot])
        stop_points.append((stop, foot))
        cut_index, cut_point = index, foot
    pieces.append([cut_point] + back_points[cut_index + 1:])

    tolerance = 1.5
    while True:
        main_line = simplify(net.points(main), tolerance)
        b_line = simplify(net.points(bypass_b), tolerance)
        c_line = simplify(net.points(bypass_c), tolerance)
        back_lines = [simplify(piece, tolerance) for piece in pieces]
        back_count = sum(len(line) - 1 for line in back_lines)
        if max(len(main_line), len(b_line), len(c_line)) + back_count <= MAX_LOOP_POINTS:
            break
        tolerance *= 1.4

    nodes: dict[str, list[float]] = {}
    index_of: dict[tuple[float, float], int] = {}

    def node(point) -> int:
        point = (round(point[0], 2), round(point[1], 2))
        if point not in index_of:
            index_of[point] = len(index_of) + 1
            nodes[str(index_of[point])] = [point[0], point[1]]
        return index_of[point]

    start, junction = node(main_line[0]), node(main_line[-1])
    edges: dict[str, list[int]] = {}

    def chain(name: str, line, counter: list[int]) -> list[str]:
        ids = []
        for a, b in zip(line, line[1:]):
            identifier = f"{name}:{counter[0]}:forward"
            edges[identifier] = [node(a), node(b)]
            ids.append(identifier)
            counter[0] += 1
        return ids

    main_ids = chain("main", main_line, [0])
    b_ids = chain("bypass-b", b_line, [0])
    c_ids = chain("bypass-c", c_line, [0])
    return_counter = [0]
    return_ids = [identifier for line in back_lines for identifier in chain("return", line, return_counter)]
    required = {f"{stop['ref']}-{slug(stop['name'])}": node(point) for stop, point in stop_points}

    used = {stop["ref"] for stop, _ in stop_points}
    avoid = densify(main_line) + densify(b_line) + densify(c_line) + [p for line in back_lines for p in densify(line)]
    peers = find_peer_loops(net, avoid, used)

    data = json.loads(json.dumps(base))
    data["metadata"]["description"] = (
        f"Integrated BE-M2-02 through BE-M2-05 fixture on real {area['name']} streets (OpenStreetMap) with real "
        "LTA bus stops; generated by src.routing.build_singapore_fleet_fixture.")
    data["geographic_origin"] = [ORIGIN_LATITUDE, ORIGIN_LONGITUDE]
    data["service_area"] = area["name"]
    data["source_geography"] = f"{area['name']}, Singapore; roads and bus stops © OpenStreetMap contributors (ODbL)"
    data["nodes"] = nodes
    data["edges"] = edges
    route_edit = data["route_edit"]
    route_edit["start_node_id"] = start
    route_edit["destination_node_id"] = start
    route_edit["default_via_node_ids"] = [next(iter(required.values()))]
    route_edit["required_stops"] = required
    route_edit["operating_node_ids"] = sorted(int(n) for n in nodes)
    route_edit["original_directed_edge_ids"] = main_ids + return_ids
    route_edit["closed_directed_edge_ids"] = [main_ids[0]]
    route_edit["detour_directed_edge_ids"] = b_ids + return_ids
    route_edit["alternative_directed_edge_paths"] = [c_ids + return_ids]
    data["peer_patterns"] = [
        {"route_ID": route_id, "waypoints": [{"x": p[0], "y": p[1]} for p in loop["ring"]]}
        for route_id, loop in zip(("route-peer-b", "route-peer-c"), peers)
    ]
    data["peer_stops"] = {route_id: f"{loop['stop']['ref']}-{slug(loop['stop']['name'])}"
                          for route_id, loop in zip(("route-peer-b", "route-peer-c"), peers)}
    # Whole metres, as the yard is laid out on axis-aligned lines from the bays: arrival then lands
    # exactly on a bay instead of a hair beside it
    depot = [float(round(net.depot[0])), float(round(net.depot[1]))]
    data["depot_id"] = f"depot-{slug(area['interchange'])}"
    data["depot_position_m"] = depot
    data["bay_positions_m"] = {"charger-1": [round(depot[0] - 15, 2), depot[1]],
                               "charger-2": [round(depot[0] + 15, 2), depot[1]]}
    data["depot_connectors"] = {
        route_edit["route_id"]: connector(net, main_line[0], main[0]),
        "route-peer-b": connector(net, peers[0]["ring"][0], peers[0]["start_node"]),
        "route-peer-c": connector(net, peers[1]["ring"][0], peers[1]["start_node"]),
    }
    data["assumptions"][0] = (f"Real {area['name']} road geometry and LTA bus stop positions from OpenStreetMap, simplified "
                              "to at most 64 waypoints a loop; energy-r1 estimates. Lanes, signals and traffic are not modelled.")
    data["assumptions"][1] = ("Shuttle-01 dwells at its loop entrance junction for 12 seconds; an obstruction on the first "
                              "segment of its main road then admits WAIT, ESCALATE, or either real street around it.")
    return data


# Index of shuttle-05, the one that starts low and is offered a charge
LOW_BATTERY_SHUTTLE = 4
# Energy it has to spare on arriving at the depot, as in the original authored fixture
LOW_BATTERY_SLACK_KWH = 0.33


def low_battery_kwh(actor, index: int) -> float:
    """A starting charge low enough to be offered a charge and enough to reach the depot.

    Uses the actor's own depot path and the energy model, so the real distance from the shuttle
    to the area's bus interchange decides it. Tests use it to make any shuttle "low".
    """
    from src.energy.decisions import LOW_LOOPS
    from src.energy.forecast import loop_energy_kwh, reserve_kwh
    from src.energy.model import ENERGY_R1, auxiliary_energy_kwh, drive_energy_kwh

    shuttle = actor.shuttles[index]
    vehicle = actor.domain.vehicles[index]
    distance = actor.depot_distances()[vehicle.vehicle_id]
    speed = actor.definition.data["speed_mps"]
    need = drive_energy_kwh(ENERGY_R1, distance) + auxiliary_energy_kwh(ENERGY_R1, distance / speed)
    reserve = reserve_kwh(ENERGY_R1, vehicle.battery_capacity_kwh)
    battery = math.ceil((reserve + need + LOW_BATTERY_SLACK_KWH) * 20) / 20
    route = actor._routes[shuttle.route_ID]
    loop = loop_energy_kwh(ENERGY_R1, route["total_length"], route["total_length"] / speed + actor.DWELL_S)
    if math.floor((battery - reserve) / loop + 1e-9) > LOW_LOOPS:
        raise ValueError(f"{vehicle.vehicle_id}'s loop is too short for its {distance:.0f} m depot trip: it could "
                         "not be low enough for a charge offer and still reach the depot")
    return battery


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--area", choices=sorted(SERVICE_AREAS), default=DEFAULT_AREA, help="Service area to build")
    parser.add_argument("--from-json", type=Path, default=None, help="Overpass answer to build from")
    parser.add_argument("--download", action="store_true", help="Fetch a fresh extract and save it first")
    parser.add_argument("--output", type=Path, default=FIXTURE)
    arguments = parser.parse_args(argv)
    area = SERVICE_AREAS[arguments.area]
    source = arguments.from_json
    if source is None:
        source = extract_path(arguments.area)
    if arguments.download:
        osm = download(area)
        source.parent.mkdir(parents=True, exist_ok=True)
        source.write_text(json.dumps(osm), encoding="utf-8")
    else:
        osm = json.loads(source.read_text(encoding="utf-8"))
    base = json.loads(FIXTURE.read_text(encoding="utf-8"))
    try:
        data = build(osm, base, area)
    except ValueError as error:
        print(error, file=sys.stderr)
        return 1
    arguments.output.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    if arguments.output.resolve() == FIXTURE.resolve():
        try:
            from src.services.integrated_actor import IntegratedSessionActor
            probe = IntegratedSessionActor(0, session_id="fixture-probe")
            data["initial_battery_kwh"][LOW_BATTERY_SHUTTLE] = low_battery_kwh(probe, LOW_BATTERY_SHUTTLE)
        except ValueError as error:
            print(error, file=sys.stderr)
            return 1
        arguments.output.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    route = data["route_edit"]
    print(f"{len(data['nodes'])} nodes, {len(data['edges'])} edges; stops {list(route['required_stops'])};"
          f" peers {data['peer_stops']}; depot {data['depot_position_m']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
