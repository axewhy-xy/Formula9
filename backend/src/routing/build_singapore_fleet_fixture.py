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

from src.routing.build_operating_network import OperatingRoads, MAX_TURN_DEG, build_network, heading_change_deg, strongly_connected

FIXTURE = Path(__file__).resolve().parents[2] / "fixtures" / "m2_integrated.json"
NETWORK_FIXTURE = Path(__file__).resolve().parents[2] / "fixtures" / "m2_operating_network.json"
OSM_DIRECTORY = Path(__file__).resolve().parents[2] / "fixtures" / "osm"
OVERPASS_URL = "https://overpass.kumi.systems/api/interpreter"

# Where a fleet can be built: a box (south, west, north, east) around the bus interchange it runs
# from, named as in OpenStreetMap. The interchange is the first depot; "other_depots" (optional)
# names more places in the box with chargers, such as bus terminals, also as in OpenStreetMap.
# Add an area here and run with --area --download to use it.
SERVICE_AREAS = {
    "toa-payoh": {"name": "Toa Payoh", "box": (1.326, 103.838, 1.344, 103.860),
                  "interchange": "Toa Payoh Interchange",
                  "other_depots": ["Saint Michael's Bus Terminal"]},
}


def depot_names(area: dict) -> list[str]:
    """Every depot of an area, its interchange first."""
    return [area["interchange"], *area.get("other_depots", [])]
DEFAULT_AREA = "toa-payoh"

QUERY = """
[out:json][timeout:180];
(
  way["highway"~"^(primary|primary_link|secondary|secondary_link|tertiary|tertiary_link|unclassified|residential|living_street|service)$"]
     ["service"!~"^(parking_aisle|driveway)$"]({box});
  way["highway"="service"]["bus"="yes"]({box});
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
    def __init__(self, osm: dict, interchange: str, depots: list[str]):
        elements = osm["elements"]
        self.position = {e["id"]: project(e["lat"], e["lon"]) for e in elements if e["type"] == "node"}
        self.loop = nx.DiGraph()
        self.access = nx.DiGraph()
        self.tags = {}
        self.way_nodes = {}
        for way in (e for e in elements if e["type"] == "way" and "highway" in e.get("tags", {})):
            tags = way["tags"]
            # Roads only buses may use lead into an interchange; depot trips use them, loops do not
            bus_only = tags.get("bus") == "yes" and tags.get("access") in ("private", "no")
            if tags.get("access") in ("private", "no") and not bus_only:
                continue
            oneway = tags.get("oneway") in ("yes", "true", "1") or tags.get("junction") in ("roundabout", "circular")
            nodes = [n for n in way["nodes"] if n in self.position]
            if tags.get("oneway") == "-1":
                nodes.reverse()
                oneway = True
            graphs = [self.access]
            if tags["highway"] in LOOP_ROADS and not bus_only:
                graphs.append(self.loop)
            if not bus_only:
                self.tags[way["id"]] = tags
                self.way_nodes[way["id"]] = nodes
            for a, b in zip(nodes, nodes[1:]):
                length = math.dist(self.position[a], self.position[b])
                for graph in graphs:
                    graph.add_edge(a, b, length=length, way=way["id"])
                    if not oneway:
                        graph.add_edge(b, a, length=length, way=way["id"])
        self.stops = []
        # Each depot's place: a node where it is, or else the middle of a way that outlines it
        nodes, outlines = {}, {}
        for element in elements:
            tags = element.get("tags", {})
            if element["type"] == "node" and tags.get("highway") == "bus_stop" and tags.get("ref"):
                self.stops.append({"ref": tags["ref"], "name": tags.get("name", tags["ref"]),
                                   "at": project(element["lat"], element["lon"])})
            name = tags.get("name")
            if name not in depots:
                continue
            if element["type"] == "node":
                nodes[name] = project(element["lat"], element["lon"])
            elif element["type"] == "way" and name not in outlines:
                corners = [self.position[n] for n in element.get("nodes", []) if n in self.position]
                if corners:
                    outlines[name] = (sum(p[0] for p in corners) / len(corners), sum(p[1] for p in corners) / len(corners))
        self.depot_places = {name: nodes.get(name, outlines.get(name)) for name in depots}
        missing = [name for name, place in self.depot_places.items() if place is None]
        if missing:
            raise ValueError(f"{', '.join(missing)} not in the downloaded area")
        # The interchange, where the loops are laid out from
        self.depot = self.depot_places[interchange]
        # Only the part of the network a shuttle can both reach and leave: the download's edges cut
        # roads off, and car-park loops are one-way pockets
        core = max(nx.strongly_connected_components(self.loop), key=len)
        self.loop = self.loop.subgraph(core).copy()
        degree = {n: len(set(self.loop.predecessors(n)) | set(self.loop.successors(n))) for n in self.loop}
        self.junctions = {n for n, d in degree.items() if d >= 3}

    def way_runs_forward(self, way: int, start, end) -> bool:
        """Whether going from start to end follows the way's own drawing order."""
        points = [self.position[n] for n in self.way_nodes[way]]
        nearest = min(range(len(points) - 1), key=lambda i: math.dist(points[i], start) + math.dist(points[i + 1], start))
        a, b = points[nearest], points[nearest + 1]
        return (b[0] - a[0]) * (end[0] - start[0]) + (b[1] - a[1]) * (end[1] - start[1]) >= 0

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


PEER_STOP_SPACING_M = 120.0
MAX_STOPS_PER_SHUTTLE = 4


def split_peer_routes(data: dict, net: Network, used: set[str]) -> None:
    """Give each shuttle on a shared loop its own route and its own real stops.

    Two shuttles run each peer loop. They keep the same roads, and take turns at the stops on its
    left kerb, so each has dedicated stops. Both still pull in at the loop's start, where it closes.
    """
    second_routes = {"route-peer-b": "route-peer-d", "route-peer-c": "route-peer-e"}
    patterns, stops_by_route, anchors = [], {}, {}
    for pattern in data["peer_patterns"]:
        route_id = pattern["route_ID"]
        points = [(p["x"], p["y"]) for p in pattern["waypoints"]]
        lengths = [math.dist(a, b) for a, b in zip(points, points[1:])]
        anchor = data["peer_stops"][route_id]
        chosen, last = [], 0.0
        for index, along, stop in stops_along(points, net.stops, used | {anchor.split("-")[0]}):
            progress = sum(lengths[:index]) + along
            if progress - last < PEER_STOP_SPACING_M or sum(lengths) - progress < PEER_STOP_SPACING_M:
                continue
            a, b = points[index], points[index + 1]
            share = along / max(lengths[index], 1e-9)
            chosen.append({"stop_id": f"{stop['ref']}-{slug(stop['name'])}", "progress_m": round(progress, 3),
                           "x": round(a[0] + (b[0] - a[0]) * share, 3), "y": round(a[1] + (b[1] - a[1]) * share, 3)})
            last = progress
        start = {"stop_id": anchor, "progress_m": 0.0, "x": points[0][0], "y": points[0][1]}
        for route, own in ((route_id, chosen[0::2]), (second_routes[route_id], chosen[1::2])):
            patterns.append({"route_ID": route, "waypoints": pattern["waypoints"]})
            stops_by_route[route] = [start, *own[:MAX_STOPS_PER_SHUTTLE]]
            anchors[route] = anchor
    data["peer_patterns"] = patterns
    data["peer_route_stops"] = stops_by_route
    data["peer_stops"] = anchors
    # shuttle-02 and -04 share the first loop, shuttle-03 and -05 the second
    data["vehicle_assignments"] = [data["vehicle_assignments"][0], "route-peer-b", "route-peer-c", "route-peer-d", "route-peer-e"]


def connector(net: Network, start_point, start_node, depot):
    """Real roads from a loop's start to the nearest point to the depot, and from the nearest point back.

    The way out is its own path: most roads here are one-way, so it is not the way in reversed, and
    it may leave from another point, as a road that reaches the depot may run on out of the area.
    """
    nearest = lambda nodes: min(nodes, key=lambda n: math.dist(net.position[n], depot))
    entry = nearest(nx.descendants(net.access, start_node) | {start_node})
    leave = nearest(nx.ancestors(net.access, start_node) | {start_node})
    way_in = [tuple(start_point)] + net.points(nx.dijkstra_path(net.access, start_node, entry, weight="length"))
    way_out = net.points(nx.dijkstra_path(net.access, leave, start_node, weight="length")) + [tuple(start_point)]
    return [list(p) for p in simplify(way_in, 2.0)], [list(p) for p in simplify(way_out, 2.0)]


def build(osm: dict, base: dict, area: dict) -> dict:
    net = Network(osm, area["interchange"], depot_names(area))
    main, bypass_b, bypass_c, back, stops = find_main_loop(net)

    # Split the return leg at its stops, so each stop is a node of the road network
    roads = OperatingRoads(net)
    back_points = net.points(back)
    back_nodes = list(back)
    stop_points, stop_nodes = [], []
    cut_point = back_points[0]
    for index, t, stop in stops[:3]:
        a, b = back_points[index], back_points[index + 1]
        share = t / max(math.dist(a, b), 1e-9)
        foot = (round(a[0] + (b[0] - a[0]) * share, 2), round(a[1] + (b[1] - a[1]) * share, 2))
        if math.dist(foot, cut_point) < 15 or math.dist(foot, back_points[-1]) < 15:
            continue
        ahead = back_nodes.index(back[index + 1])
        stop_node = roads.insert_stop(back_nodes[ahead - 1], back[index + 1], foot)
        back_nodes.insert(ahead, stop_node)
        stop_points.append((stop, foot))
        stop_nodes.append(stop_node)
        cut_point = foot

    network = build_network(roads, [("main", main), ("bypass-b", bypass_b), ("bypass-c", bypass_c), ("return", back_nodes)],
                            simplify, curated_width_m=base["route_edit"]["road_width_m"])
    if not strongly_connected(network):
        raise ValueError("The operating network has roads a shuttle could not leave")
    edges = {identifier: [network["edges"][identifier]["source"], network["edges"][identifier]["target"]]
             for ids in network["chains"].values() for identifier in ids}
    nodes = {str(n): network["nodes"][str(n)] for n in sorted({n for pair in edges.values() for n in pair})}
    main_ids, b_ids, c_ids, return_ids = (network["chains"][name] for name in ("main", "bypass-b", "bypass-c", "return"))
    start = network["node_ids"][main[0]]
    required = {f"{stop['ref']}-{slug(stop['name'])}": network["node_ids"][node]
                for (stop, _), node in zip(stop_points, stop_nodes)}
    # A stop is only served from its own kerb: arriving along the return leg
    arrivals = {network["edges"][identifier]["target"]: identifier for identifier in return_ids}
    approaches = {stop: [arrivals[node]] for stop, node in required.items()}
    for path in (main_ids + return_ids, b_ids + return_ids, c_ids + return_ids):
        for first, second in zip(path, path[1:]):
            turn = heading_change_deg(network["edges"][first]["path"], network["edges"][second]["path"])
            if turn > MAX_TURN_DEG:
                raise ValueError(f"The scenario's own route turns {turn:.0f} degrees from {first} to {second}")
    # The streets of the main loop, which the peers must keep clear of
    main_line, b_line, c_line = net.points(main), net.points(bypass_b), net.points(bypass_c)
    back_line = [roads.position[n] for n in back_nodes]

    used = {stop["ref"] for stop, _ in stop_points}
    avoid = densify(main_line) + densify(b_line) + densify(c_line) + densify(back_line)
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
    route_edit["network_fixture"] = NETWORK_FIXTURE.name
    route_edit["start_node_id"] = start
    route_edit["destination_node_id"] = start
    route_edit["default_via_node_ids"] = [next(iter(required.values()))]
    route_edit["required_stops"] = required
    route_edit["stop_approach_edge_ids"] = approaches
    route_edit["operating_node_ids"] = sorted(int(n) for n in nodes)
    route_edit["original_directed_edge_ids"] = main_ids + return_ids
    # The whole main road and, where it is two-way, its other direction: a closure shuts the road
    closed_back = [identifier.replace(":forward", ":reverse") for identifier in main_ids]
    route_edit["closed_directed_edge_ids"] = main_ids + [identifier for identifier in closed_back if identifier in network["edges"]]
    route_edit["detour_directed_edge_ids"] = b_ids + return_ids
    route_edit["alternative_directed_edge_paths"] = [c_ids + return_ids]
    data["operating_network"] = {key: network[key] for key in ("nodes", "edges", "chains", "rules")}
    data["peer_patterns"] = [
        {"route_ID": route_id, "waypoints": [{"x": p[0], "y": p[1]} for p in loop["ring"]]}
        for route_id, loop in zip(("route-peer-b", "route-peer-c"), peers)
    ]
    data["peer_stops"] = {route_id: f"{loop['stop']['ref']}-{slug(loop['stop']['name'])}"
                          for route_id, loop in zip(("route-peer-b", "route-peer-c"), peers)}
    split_peer_routes(data, net, used)
    # The single-depot keys of older fixtures; "depots" below replaces them
    for key in ("depot_id", "depot_position_m", "bay_positions_m", "depot_connectors"):
        data.pop(key, None)
    # Every depot, each with its own two charger bays and a road in from each loop. Charger IDs
    # run on across depots, so each is unique; the first depot keeps charger-1 and charger-2.
    # Positions are whole metres, as the yard is laid out on axis-aligned lines from the bays:
    # arrival then lands exactly on a bay instead of a hair beside it.
    data["depots"] = []
    for index, name in enumerate(depot_names(area)):
        place = net.depot_places[name]
        at = [float(round(place[0])), float(round(place[1]))]
        first, second = f"charger-{index * 2 + 1}", f"charger-{index * 2 + 2}"
        # Roads are chosen from the exact place, as for the original depot, so its paths stay the same
        trips = {
            route_edit["route_id"]: connector(net, net.position[main[0]], main[0], place),
            "route-peer-b": connector(net, peers[0]["ring"][0], peers[0]["start_node"], place),
            "route-peer-c": connector(net, peers[1]["ring"][0], peers[1]["start_node"], place),
        }
        # The second shuttle on each loop leaves it for the depot the same way
        trips["route-peer-d"] = trips["route-peer-b"]
        trips["route-peer-e"] = trips["route-peer-c"]
        data["depots"].append({"depot_id": f"depot-{slug(name)}", "position_m": at,
                               "bays": {first: [round(at[0] - 15, 2), at[1]], second: [round(at[0] + 15, 2), at[1]]},
                               "connectors": {route: way_in for route, (way_in, _) in trips.items()},
                               "exits": {route: way_out for route, (_, way_out) in trips.items()}})
    data["assumptions"][0] = (f"Real {area['name']} road geometry and LTA bus stop positions from OpenStreetMap, simplified "
                              "to at most 64 waypoints a loop; energy-r1 estimates. Lanes, signals and traffic are not modelled.")
    data["assumptions"][1] = ("Shuttle-01 dwells at its loop entrance junction for 12 seconds; an obstruction on the first "
                              "segment of its main road then admits WAIT, ESCALATE, or either real street around it.")
    return data


# Index of shuttle-05, the one that starts low and is offered a charge
LOW_BATTERY_SHUTTLE = 4
# Energy it has to spare on arriving at the depot, as in the original authored fixture
LOW_BATTERY_SLACK_KWH = 0.33
# Index of shuttle-03 and its start: 20.8% of 40 kWh, just above the client's 20% battery low
# sound, so a trainee hears it within the first minutes of driving
NEAR_LOW_SHUTTLE = 2
NEAR_LOW_BATTERY_KWH = 8.32


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


def write_network(network: dict, path: Path) -> None:
    """One edge a line, so a rebuild's diff shows which roads changed."""
    def block(name: str, values: dict) -> str:
        lines = ",\n".join(f"  {json.dumps(key)}: {json.dumps(value, ensure_ascii=False)}" for key, value in values.items())
        return f'"{name}": {{\n{lines}\n}}'
    path.write_text("{\n" + ",\n".join(block(name, network[name]) for name in ("rules", "chains", "nodes", "edges")) + "\n}\n",
                    encoding="utf-8")


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
    network = data.pop("operating_network")
    write_network(network, arguments.output.parent / NETWORK_FIXTURE.name)
    arguments.output.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    if arguments.output.resolve() == FIXTURE.resolve():
        try:
            from src.services.integrated_actor import IntegratedSessionActor
            probe = IntegratedSessionActor(0, session_id="fixture-probe")
            data["initial_battery_kwh"][LOW_BATTERY_SHUTTLE] = low_battery_kwh(probe, LOW_BATTERY_SHUTTLE)
            data["initial_battery_kwh"][NEAR_LOW_SHUTTLE] = NEAR_LOW_BATTERY_KWH
        except ValueError as error:
            print(error, file=sys.stderr)
            return 1
        arguments.output.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    route = data["route_edit"]
    print(f"{len(network['nodes'])} network nodes, {len(network['edges'])} network edges; "
          f"{len(data['nodes'])} scenario nodes, {len(data['edges'])} scenario edges; stops {list(route['required_stops'])};"
          f" peers {data['peer_stops']}; depots {[d['position_m'] for d in data['depots']]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
