"""Curate the service area's roads into the operating network the route editor works on.

Called by build_singapore_fleet_fixture, which owns the scenario's named road chains. Every road
a shuttle can both reach and leave becomes directed edges between junctions, each carrying its
drawn geometry and the operating metadata validation needs. Tags recorded in OpenStreetMap win; the
rest come from the declared rules below, and each value names its source. A road with no basis
for a value leaves it null: validation reports it as unknown instead of guessing.
"""

from __future__ import annotations

from collections import defaultdict
import math
import re

import networkx as nx

# What this exercise assumes of a lane where OSM gives lanes but no width.
LANE_WIDTH_M = {"service": 3.0}
DEFAULT_LANE_WIDTH_M = 3.5
# These classes are public roads, with at least one usable lane each way even where lanes are unmapped.
LANE_ASSUMED_CLASSES = {"primary", "primary_link", "secondary", "secondary_link", "tertiary", "tertiary_link",
                        "unclassified", "residential"}
# Headroom on an open road with no recorded limit: the scenario's existing road_height_m.
OPEN_ROAD_HEADROOM_M = 4.0
GEOMETRY_TOLERANCE_M = 1.5
# A turn sharper than this is a U-turn. A real junction movement stays well inside it.
MAX_TURN_DEG = 165.0


def number(text) -> float | None:
    match = re.match(r"^\s*([0-9]+(?:\.[0-9]+)?)\s*m?\s*$", str(text)) if text is not None else None
    return float(match.group(1)) if match else None


def _lanes(tags: dict, oneway: bool, forward: bool) -> float | None:
    if oneway:
        return number(tags.get("lanes"))
    own = number(tags.get("lanes:forward" if forward else "lanes:backward"))
    total = number(tags.get("lanes"))
    return own if own else max(1.0, total // 2) if total else None


def road_metadata(tags: dict, oneway: bool, forward: bool, *, curated: bool, curated_width_m: float) -> dict:
    """The operating facts about one direction of a road, each with where it came from."""
    road_class = tags.get("highway", "unknown")
    width, width_source = None, None
    tagged = number(tags.get("width"))
    lanes = _lanes(tags, oneway, forward)
    lane_width = LANE_WIDTH_M.get(road_class, DEFAULT_LANE_WIDTH_M)
    if tagged:
        width, width_source = (tagged if oneway else tagged / 2), "osm:width"
    elif lanes:
        width, width_source = lanes * lane_width, "osm:lanes"
    elif curated:
        width, width_source = curated_width_m, "scenario_route"
    elif road_class in LANE_ASSUMED_CLASSES:
        width, width_source = lane_width, "road_class"
    height, height_source = number(tags.get("maxheight")), "osm:maxheight"
    if height is None:
        enclosed = "tunnel" in tags or tags.get("covered") == "yes"
        height, height_source = (None, None) if enclosed else (OPEN_ROAD_HEADROOM_M, "no_recorded_limit")
    access = None
    if tags.get("bus") == "no" or tags.get("motor_vehicle") == "no":
        access = "closed_to_buses"
    elif tags.get("access") == "destination":
        access = "destination_only"
    return {"road": tags.get("name"), "class": road_class, "width_m": width, "width_source": width_source,
            "height_m": height, "height_source": height_source, "access": access}


class OperatingRoads:
    """The fixture's road graph, split into junction-to-junction runs."""

    def __init__(self, net):
        self.net = net
        self.graph = net.loop.copy()
        self.position = dict(net.position)
        self.forced_breaks: set[int] = set()
        self._next_stop_node = -1

    def insert_stop(self, a: int, b: int, foot: tuple[float, float]) -> int:
        """Put a stop's kerbside point on the road between two nodes, in both directions if two-way."""
        node = self._next_stop_node
        self._next_stop_node -= 1
        self.position[node] = (round(foot[0], 2), round(foot[1], 2))
        for u, v in ((a, b), (b, a)):
            if self.graph.has_edge(u, v):
                way = self.graph[u][v]["way"]
                self.graph.remove_edge(u, v)
                self.graph.add_edge(u, node, way=way)
                self.graph.add_edge(node, v, way=way)
        self.forced_breaks.add(node)
        return node

    def _neighbours(self, node: int) -> set[int]:
        return set(self.graph.predecessors(node)) | set(self.graph.successors(node))

    def breaks(self) -> set[int]:
        result = set(self.forced_breaks)
        for node in self.graph:
            near = self._neighbours(node)
            if len(near) != 2:
                result.add(node)
                continue
            # A change of road changes the metadata, so an edge never spans two roads
            ways = {self.graph[u][v]["way"] for u, v in self.graph.in_edges(node)} | \
                   {self.graph[u][v]["way"] for u, v in self.graph.out_edges(node)}
            if len(ways) > 1:
                result.add(node)
        return result

    def runs(self, breaks: set[int]) -> list[tuple[int, ...]]:
        result = []
        for start in sorted(breaks):
            for step in sorted(self.graph.successors(start)):
                run = [start, step]
                while run[-1] not in breaks:
                    ahead = [n for n in self.graph.successors(run[-1]) if n != run[-2]]
                    if len(ahead) != 1:
                        break
                    run.append(ahead[0])
                result.append(tuple(run))
        return result

    def chain_runs(self, nodes: list[int], breaks: set[int]) -> list[tuple[int, ...]]:
        runs, current = [], [nodes[0]]
        for node in nodes[1:]:
            current.append(node)
            if node in breaks or node == nodes[-1]:
                runs.append(tuple(current))
                current = [node]
        return runs


def build_network(roads: OperatingRoads, chains: list[tuple[str, list[int]]], simplify, *, curated_width_m: float) -> dict:
    """Name every directed run; a chain's own road keeps the chain's name, as the scenario refers to it.
    "runs" maps each run, as its nodes of the road graph, to its edge ID."""
    net = roads.net
    breaks = roads.breaks() | {node for _, nodes in chains for node in (nodes[0], nodes[-1])}
    node_ids: dict[int, int] = {}

    def node_id(node: int) -> int:
        return node_ids.setdefault(node, len(node_ids) + 1)

    edges: dict[str, dict] = {}
    # One road is one edge, whichever chains use it, so blocking it blocks all of them.
    claimed: dict[tuple[int, ...], str] = {}

    def add(identifier: str, run: tuple[int, ...], *, curated: bool) -> None:
        a, b = run[0], run[1]
        way = roads.graph[a][b]["way"]
        tags = net.tags[way]
        oneway = not all(roads.graph.has_edge(v, u) for u, v in zip(run, run[1:]))
        forward = net.way_runs_forward(way, roads.position[a], roads.position[b])
        path = simplify([roads.position[n] for n in run], GEOMETRY_TOLERANCE_M)
        edges[identifier] = {"source": node_id(run[0]), "target": node_id(run[-1]),
                             "path": [[round(x, 2), round(y, 2)] for x, y in path],
                             **road_metadata(tags, oneway, forward, curated=curated, curated_width_m=curated_width_m)}

    chain_ids: dict[str, list[str]] = {}
    for name, nodes in chains:
        ids = []
        count = 0
        for run in roads.chain_runs(nodes, breaks):
            if run not in claimed:
                claimed[run] = f"{name}:{count}:forward"
                count += 1
                add(claimed[run], run, curated=True)
            ids.append(claimed[run])
        chain_ids[name] = ids
    for name in chain_ids:
        for identifier in chain_ids[name]:
            if not identifier.startswith(name + ":"):
                continue
            run = next(key for key, value in claimed.items() if value == identifier)
            back = tuple(reversed(run))
            if back not in claimed and all(roads.graph.has_edge(u, v) for u, v in zip(back, back[1:])):
                claimed[back] = identifier.replace(":forward", ":reverse")
                add(claimed[back], back, curated=True)

    per_way = defaultdict(int)
    for run in sorted(roads.runs(breaks)):
        if run in claimed:
            continue
        back = tuple(reversed(run))
        twin = back not in claimed and all(roads.graph.has_edge(u, v) for u, v in zip(back, back[1:]))
        way = roads.graph[run[0]][run[1]]["way"]
        index = per_way[way]
        per_way[way] += 1
        claimed[run] = f"road-{way}:{index}:forward"
        add(claimed[run], run, curated=False)
        if twin:
            claimed[back] = f"road-{way}:{index}:reverse"
            add(claimed[back], back, curated=False)
    return {"nodes": {str(identifier): [*roads.position[node]] for node, identifier in sorted(node_ids.items(), key=lambda p: p[1])},
            "edges": edges, "chains": chain_ids, "node_ids": node_ids, "runs": claimed,
            "rules": {"max_turn_deg": MAX_TURN_DEG, "default_lane_width_m": DEFAULT_LANE_WIDTH_M,
                      "open_road_headroom_m": OPEN_ROAD_HEADROOM_M}}


def heading_change_deg(first: list, second: list) -> float:
    """How far a shuttle turns from the end of one drawn path onto the start of the next."""
    (ax, ay), (bx, by) = first[-2], first[-1]
    (cx, cy), (dx, dy) = second[0], second[1]
    change = math.degrees(math.atan2(dy - cy, dx - cx) - math.atan2(by - ay, bx - ax))
    return abs((change + 180) % 360 - 180)


def strongly_connected(network: dict) -> bool:
    graph = nx.DiGraph((edge["source"], edge["target"]) for edge in network["edges"].values())
    return nx.is_strongly_connected(graph)
