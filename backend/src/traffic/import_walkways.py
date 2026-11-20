"""Sidewalks along an area's roads, as triangles the 3D city view can draw. Needs no download.

Run from backend/: python -m src.traffic.import_walkways [--area toa-payoh]

Reads the prepared roads (fixtures/traffic/<area>.json), their OpenStreetMap tags (fixtures/osm), LTA's
crossings (fixtures/traffic/<area>-crossings.json) and the building outlines (fixtures/buildings), and
writes fixtures/traffic/<area>-walkways.json. Positions are local metres in the frame the fleet map uses
(x east and y north of central Singapore).

This is the only place sidewalk geometry is worked out; the views only draw what it wrote. A sidewalk
is a band each side of a road that has one, so the bands of roads meeting at a junction merge into one
paved corner, and every road's asphalt is then cut out of them, so a sidewalk never covers a road. The
asphalt cut out is the shape the 3D view draws (RoadMesh.cs): a mitred ribbon with twelve-sided caps at
ends and sharp bends. Where a crossing meets a sidewalk, the kerb drops to the road in a ramp.

The file also holds the walk network (phase 2 of the sidewalks plan, sidewalks_plan.md), which the backend
and the views both read, so a walker at a distance along a walkway is at the same spot everywhere:

- sidewalk walkways run down the middle of each footpath, cut where the paving ends and joined where they
  meet, so they always lie on the sidewalk drawn;
- link walkways join the ends left over round a corner or a dead end, along a line that stays on the paving
  (or, from a crossing's landing with no sidewalk, over bare ground but never asphalt or a building);
- crossing walkways run kerb to kerb along LTA's crossings, from a kerb node split into the sidewalk beside
  each end. A signalled one carries the junction and phase whose red lets people over; one whose light
  cannot be told is left out, never left free to cross on any light. LTA maps no crossings over side
  streets and driveways, so the footpaths would stop at every one: where a minor road's mouth meets a
  footpath on both sides, and no LTA crossing of that road is within 50 m, an uncontrolled crossing (no
  light, no priority) carries the footpath over it, with ramps of its own;
- seeds are where walkers start, spread along the network's main connected part.

Rebuild the Unity copy with --unity-copy ../Assets/Formula9/Resources/Formula9/RoadData/<area>-walkways.json.
"""

from __future__ import annotations

import argparse
import bisect
import hashlib
import json
import math
import random
import sys
from pathlib import Path

import shapely
import shapely.ops
from shapely.geometry import LineString, Point, Polygon
from shapely.geometry.polygon import orient

from src.routing.build_singapore_fleet_fixture import DEFAULT_AREA, SERVICE_AREAS, extract_path
from src.routing.import_buildings import OUTPUT_DIRECTORY as BUILDINGS_DIRECTORY, read_buildings
from src.traffic.import_road_features import CROSSING_WIDTH_M, output_path
from src.traffic.import_vehicle_population import OUTPUT_DIRECTORY, ROADS_ATTRIBUTION

VERSION = 2

# The road surface the 3D view draws. These must equal RoadMesh.cs (a test reads them from there).
LANE_WIDTH_M = 3.2
SHOULDER_M = 0.6
SURFACE_Y = 0.1
SHARP_BEND_COSINE = 0.7
MAX_MITRE = 2.0
CAP_SIDES = 12
MIN_SEGMENT_M = 1e-3

# The sidewalk's own sizes. These must equal SidewalkMesh.cs (a test reads them from there).
KERB_RISE_M = 0.15
KERB_STONE_M = 0.15
RAMP_DEPTH_M = 1.5
RAMP_WIDTH_M = CROSSING_WIDTH_M

# What each class of road has when OpenStreetMap does not say: (sides, verge m, footpath m, walking sub-lanes).
# "both" puts a sidewalk each side of a two-way road but only on the kerb side (the left, as Singapore drives
# on the left) of a one-way one: most one-way roads here are one half of a dual carriageway, with a central
# divider on their right.
CLASSES = {
    "primary": ("both", 1.0, 2.4, 3), "secondary": ("both", 1.0, 2.4, 3),
    "tertiary": ("both", 0.5, 1.8, 2), "unclassified": ("both", 0.5, 1.8, 2),
    "residential": ("both", 0.5, 1.8, 2), "living_street": ("both", 0.5, 1.8, 2),
    "primary_link": ("none", 0.0, 1.5, 2), "secondary_link": ("none", 0.0, 1.5, 2),
    "tertiary_link": ("none", 0.0, 1.5, 2), "service": ("none", 0.0, 1.5, 2),
}
OTHER_CLASS = ("none", 0.0, 1.5, 2)

# Small detail is dropped: bends gentler than this, and pieces of sidewalk smaller than this
SIMPLIFY_M = 0.05
MIN_PIECE_M2 = 1.0
# Every corner is on a centimetre grid, so the views can share vertices exactly
GRID_M = 0.01
# Sidewalk edges this close to the asphalt are the kerb, the rest face the ground
KERB_EDGE_M = 0.05
# A crossing's kerb is looked for this far past the end of LTA's walking line
RAMP_REACH_M = 8.0
# How much wider and deeper than its ramp a crossing's landing is, where it has no sidewalk to land on
LANDING_MARGIN_M = 0.5
# A ramp smaller than this is not worth one: the crossing has no sidewalk to land on
MIN_RAMP_M2 = 0.5
# Triangles are grouped in squares this size, so the view can leave out those it cannot see
TILE_M = 250.0
ARC_STEPS = 6

# The walk network. A walking line keeps this far inside the paving's edge
WALK_INSET_M = 0.3
# Walkway ends closer than this are one node (the lowest numbered is kept)
MERGE_NODE_M = 1.6
# A stub this short left past where two walking lines cross (into a corner's kerb) is cut off
STUB_M = 4.0
# Ends left over are joined to up to this many others this close, along a line that stays on the paving
LINK_REACH_M = 25.0
LINKS_PER_END = 2
LINK_CURVE_STEPS = 8
# A crossing's end steps onto a walkway this close to its kerb. Where its kerb is missing or opens onto more
# asphalt, the end is looked for along its line in steps this long
CROSSING_REACH_M = 16.0
LANDING_STEP_M = 0.25
# A signal approach governs a crossing when its stop line is this close and its traffic runs this nearly across
MATCH_SIGNAL_REACH_M = 45.0
MATCH_SIGNAL_MAX_ALIGNMENT = 0.6
# Where a footpath stops at a minor road's mouth (a side street or a driveway) and carries on across it, people
# cross there, as Singapore allows anywhere not within 50 m of a crossing. Such a crossing has no light and
# no priority: walkers wait for a gap in the traffic.
UNCONTROLLED_CLASSES = frozenset({"residential", "living_street", "unclassified", "service", "tertiary"})
UNCONTROLLED_CLEAR_OF_CROSSING_M = 50.0
# People walk side by side in sub-lanes this wide
SUBLANE_M = 0.75
LINK_WIDTH_M = 1.5
# Walkers: how many, where they start and how they look. The count is the file's, not the code's (plan section 9)
SEED_COUNT = 600
SEED = 29
SEED_SPEED_MPS = (1.0, 1.6)
SEED_HEIGHT_M = (1.5, 1.9)
SEED_MODELS = 8
# The network's largest connected part must hold this share of its length, or a rule broke it. The plan asked
# for 90%, but Toa Payoh's sidewalks only join across its main roads at LTA's crossings, and those are mapped
# in the middle of the estate only: its southern and western edges are islands, joined by footpaths
# OpenStreetMap maps as ways of their own (sidewalk=separate, plan phase 5). The area holds about 61% today.
MIN_MAIN_SHARE = 0.55


def half_width(road: dict) -> float:
    """Half the asphalt's width, as RoadMesh.HalfWidth draws it: every lane, plus a shoulder each side."""
    lanes = road["lanes"] if road["oneway"] else road["lanes"] * 2
    return lanes * LANE_WIDTH_M / 2 + SHOULDER_M


def class_of(road: dict) -> tuple[str, float, float, int]:
    return CLASSES.get(road["class"], OTHER_CLASS)


SIDE_VALUES = {"both": (True, True), "yes": (True, True), "separate": (True, True), "left": (True, False),
               "right": (False, True), "no": (False, False), "none": (False, False)}
YES = {"yes": True, "separate": True, "no": False, "none": False}


def sidewalk_sides(road: dict, tags: dict) -> tuple[bool, bool]:
    """Whether the road has a sidewalk (left, right) of its points, which run the way traffic does.

    OpenStreetMap's sidewalk, sidewalk:both, sidewalk:left and sidewalk:right override the class's default.
    Its left and right are of the way as drawn, which runs against the traffic on a oneway=-1 way, whose
    points the prepared roads turn round. "separate" means a footpath mapped as its own way; until those
    are imported it counts as a sidewalk here.
    """
    default = class_of(road)[0]
    left = right = default == "both"
    if road["oneway"]:
        right = False
    drawn_left, drawn_right = None, None
    if tags.get("sidewalk") in SIDE_VALUES:
        drawn_left, drawn_right = SIDE_VALUES[tags["sidewalk"]]
    if tags.get("sidewalk:both") in YES:
        drawn_left = drawn_right = YES[tags["sidewalk:both"]]
    if tags.get("sidewalk:left") in YES:
        drawn_left = YES[tags["sidewalk:left"]]
    if tags.get("sidewalk:right") in YES:
        drawn_right = YES[tags["sidewalk:right"]]
    if tags.get("oneway") == "-1":
        drawn_left, drawn_right = drawn_right, drawn_left
    return (left if drawn_left is None else drawn_left, right if drawn_right is None else drawn_right)


def line_of(road: dict) -> list[tuple[float, float]]:
    """The road's points without repeats, as RoadMesh.Line keeps them."""
    points: list[tuple[float, float]] = []
    for x, y in road["points_m"]:
        if not points or math.dist(points[-1], (x, y)) > MIN_SEGMENT_M:
            points.append((float(x), float(y)))
    return points


def unit(dx: float, dy: float) -> tuple[float, float]:
    length = math.hypot(dx, dy)
    return (dx / length, dy / length) if length > 0 else (0.0, 0.0)


def is_sharp(before, at, after) -> bool:
    a = unit(at[0] - before[0], at[1] - before[1])
    b = unit(after[0] - at[0], after[1] - at[1])
    return a[0] * b[0] + a[1] * b[1] < SHARP_BEND_COSINE


def cap(center: tuple[float, float], radius: float) -> Polygon:
    """RoadMesh.Cap: a twelve-sided disc, its corners at the same angles."""
    return Polygon([(center[0] + radius * math.cos(-2 * math.pi * i / CAP_SIDES),
                     center[1] + radius * math.sin(-2 * math.pi * i / CAP_SIDES)) for i in range(CAP_SIDES)])


def carriageway(points: list[tuple[float, float]], radius: float) -> list[Polygon]:
    """The asphalt RoadMesh draws for a road: a mitred ribbon, capped at its ends and sharp bends."""
    parts = [LineString(points).buffer(radius, cap_style="flat", join_style="mitre", mitre_limit=MAX_MITRE)]
    for index, point in enumerate(points):
        if index in (0, len(points) - 1) or is_sharp(points[index - 1], point, points[index + 1]):
            parts.append(cap(point, radius))
    return parts


def corner(center: tuple[float, float], radius: float, side: tuple[float, float], onward: tuple[float, float]) -> Polygon:
    """A quarter disc from a road's end, between its side and the way on past its end.

    A side band stops square at the road's end; this rounds it off, so where a road bends at a junction
    the outside of the bend is paved, and a dead end's sidewalk turns its corners.
    """
    start = math.atan2(side[1], side[0])
    turn = math.atan2(side[0] * onward[1] - side[1] * onward[0], side[0] * onward[0] + side[1] * onward[1])
    arc = [(center[0] + radius * math.cos(start + turn * step / ARC_STEPS),
            center[1] + radius * math.sin(start + turn * step / ARC_STEPS)) for step in range(ARC_STEPS + 1)]
    return Polygon([center, *arc])


def side_band(points: list[tuple[float, float]], reach: float, left: bool) -> list[Polygon]:
    """Everything from a road's centreline to reach metres out on one side, rounded off at its ends."""
    line = LineString(points)
    band = line.buffer(reach if left else -reach, single_sided=True, join_style="mitre", mitre_limit=MAX_MITRE)
    parts = [band]
    sign = 1 if left else -1
    for end, inner, last in ((points[0], points[1], False), (points[-1], points[-2], True)):
        # Onward is out past this end: the way of travel at the last point, against it at the first
        onward = unit(end[0] - inner[0], end[1] - inner[1])
        travel = onward if last else (-onward[0], -onward[1])
        normal = (-travel[1] * sign, travel[0] * sign)
        parts.append(corner(end, reach, normal, onward))
    return parts


def union(parts) -> shapely.Geometry:
    return shapely.union_all([part for part in parts if part is not None and not part.is_empty], grid_size=GRID_M)


def polygons(geometry) -> list[Polygon]:
    """The polygons in any geometry, collections opened up, lines and points left out."""
    found = []
    for part in shapely.get_parts(geometry):
        if isinstance(part, Polygon):
            if not part.is_empty:
                found.append(part)
        elif hasattr(part, "geoms"):
            found.extend(polygons(part))
    return found


def drop_small(geometry, smallest: float) -> shapely.Geometry:
    kept = [part for part in polygons(geometry) if part.area >= smallest]
    return shapely.union_all(kept, grid_size=GRID_M) if kept else Polygon()


def kerb_point(crossing_line: list[list[float]], end: int, roads_area, roads_edge) -> tuple[tuple[float, float], tuple[float, float]] | None:
    """Where one end of a crossing meets the kerb, and the way out of the road there.

    The kerb is where the crossing's line, carried on a little past LTA's end, last leaves the asphalt.
    """
    a = crossing_line[0] if end == 0 else crossing_line[-1]
    b = crossing_line[-1] if end == 0 else crossing_line[0]
    outward = unit(a[0] - b[0], a[1] - b[1])
    if outward == (0.0, 0.0):
        return None
    middle = ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)
    far = (a[0] + outward[0] * RAMP_REACH_M, a[1] + outward[1] * RAMP_REACH_M)
    hits = shapely.intersection(LineString([middle, far]), roads_edge)
    points = [(x, y) for part in shapely.get_parts(hits) for x, y in shapely.get_coordinates(part)]
    if not points:
        return None
    # Of the kerbs along the line, the one nearest LTA's end, so a median island is not taken for the far kerb
    kerb = min(points, key=lambda point: math.dist(point, a))
    # The kerb's own outward direction, from the asphalt's nearest edge to just outside it
    probe = Point(kerb[0] + outward[0] * 0.5, kerb[1] + outward[1] * 0.5)
    if not roads_area.contains(probe):
        nearest = shapely.ops.nearest_points(probe, roads_edge)[1]
        away = unit(probe.x - nearest.x, probe.y - nearest.y)
        if away != (0.0, 0.0) and away[0] * outward[0] + away[1] * outward[1] > 0.3:
            outward = away
    return (round(kerb[0], 2), round(kerb[1], 2)), (round(outward[0], 4), round(outward[1], 4))


def ramp_shape(kerb: tuple[float, float], outward: tuple[float, float]) -> Polygon:
    """A crossing-wide strip from just inside the kerb to the ramp's depth: clipped to the sidewalk, it is the ramp."""
    along = (-outward[1], outward[0])
    half = RAMP_WIDTH_M / 2
    corners = []
    for side, depth in ((-1, -0.2), (1, -0.2), (1, RAMP_DEPTH_M), (-1, RAMP_DEPTH_M)):
        corners.append((kerb[0] + along[0] * half * side + outward[0] * depth,
                        kerb[1] + along[1] * half * side + outward[1] * depth))
    return Polygon(corners)


def landing_shape(kerb: tuple[float, float], outward: tuple[float, float]) -> Polygon:
    """Somewhere to stand at a crossing's end that has no sidewalk: a little wider than the crossing, a ramp deep."""
    along = (-outward[1], outward[0])
    half = RAMP_WIDTH_M / 2 + LANDING_MARGIN_M
    depth = RAMP_DEPTH_M + LANDING_MARGIN_M
    return Polygon([(kerb[0] + along[0] * half * side + outward[0] * reach, kerb[1] + along[1] * half * side + outward[1] * reach)
                    for side, reach in ((-1, -0.2), (1, -0.2), (1, depth), (-1, depth))])


def cm(value: float) -> int:
    return int(round(value * 100))


class Tile:
    """One square of the drawing: its corners, the triangles of each zone and the edges that get faces."""

    def __init__(self, key: tuple[int, int]):
        self.key = key
        self.index: dict[tuple[int, int], int] = {}
        self.vertices: list[int] = []
        self.zones: dict[str, list[int]] = {"footpath": [], "kerb_stone": [], "verge": []}
        self.ramps: list[list[int]] = []
        self.ramp_index: dict[int, int] = {}
        self.ramp_triangles: list[int] = []
        self.edges: dict[str, list[int]] = {"kerb_edges": [], "outer_edges": [], "cheek_edges": []}

    def vertex(self, point: tuple[int, int]) -> int:
        found = self.index.get(point)
        if found is None:
            found = self.index[point] = len(self.vertices) // 2
            self.vertices.extend(point)
        return found

    def ramp(self, number: int, ramps: list[list[int]]) -> int:
        found = self.ramp_index.get(number)
        if found is None:
            found = self.ramp_index[number] = len(self.ramps)
            self.ramps.append(ramps[number])
        return found

    def json(self) -> dict:
        return {"key": list(self.key), "vertices_cm": self.vertices, "zones": self.zones, "ramps": self.ramps,
                "ramp_triangles": self.ramp_triangles, **self.edges}


def tile_key(x_cm: float, y_cm: float) -> tuple[int, int]:
    return (math.floor(x_cm / 100 / TILE_M), math.floor(y_cm / 100 / TILE_M))


def rings_cm(polygon: Polygon) -> list[list[tuple[int, int]]]:
    """A polygon's rings in centimetres, the outline anticlockwise and holes clockwise, so its inside is on each edge's left."""
    polygon = orient(polygon, 1.0)
    rings = [polygon.exterior, *polygon.interiors]
    found = []
    for ring in rings:
        points = [(cm(x), cm(y)) for x, y in ring.coords]
        found.append([point for index, point in enumerate(points) if index == 0 or point != points[index - 1]])
    return found


def triangles_cm(polygon: Polygon) -> list[tuple[tuple[int, int], ...]]:
    """A polygon cut into triangles on its own corners, each clockwise seen from above (so it faces up in Unity)."""
    found = []
    for triangle in shapely.get_parts(shapely.constrained_delaunay_triangles(polygon)):
        a, b, c = [(cm(x), cm(y)) for x, y in list(triangle.exterior.coords)[:3]]
        cross = (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])
        if cross == 0:
            continue
        found.append((a, b, c) if cross < 0 else (a, c, b))
    return found


def sublanes(width: float) -> int:
    """How many people can walk side by side on a walkway this wide."""
    return max(1, math.floor(width / SUBLANE_M + 1e-9))


class WalkGraph:
    """Nodes and undirected walkways between them. Asking for a node near one that exists gives that one."""

    def __init__(self):
        self.nodes: list[tuple[float, float]] = []
        self.ways: list[dict | None] = []
        self.at_node: list[list[int]] = []
        self.grid: dict[tuple[int, int], list[int]] = {}

    def node(self, point) -> int:
        x, y = round(float(point[0]), 2), round(float(point[1]), 2)
        cell = (math.floor(x / MERGE_NODE_M), math.floor(y / MERGE_NODE_M))
        best, best_distance = -1, MERGE_NODE_M
        for gx in (cell[0] - 1, cell[0], cell[0] + 1):
            for gy in (cell[1] - 1, cell[1], cell[1] + 1):
                for index in self.grid.get((gx, gy), ()):
                    distance = math.dist(self.nodes[index], (x, y))
                    if distance < best_distance or (distance == best_distance and index < best):
                        best, best_distance = index, distance
        if best >= 0:
            return best
        self.nodes.append((x, y))
        self.at_node.append([])
        self.grid.setdefault(cell, []).append(len(self.nodes) - 1)
        return len(self.nodes) - 1

    def add(self, kind: str, points, ends: tuple[int, int] | None = None, **fields) -> int | None:
        """A walkway along the points, from and to the nodes at its ends; None if they are one node."""
        start, end = ends if ends is not None else (self.node(points[0]), self.node(points[-1]))
        if start == end:
            return None
        line = [self.nodes[start], *[(round(float(x), 2), round(float(y), 2)) for x, y in points[1:-1]], self.nodes[end]]
        line = [point for index, point in enumerate(line) if index == 0 or point != line[index - 1]]
        if len(line) < 2:
            return None
        index = len(self.ways)
        self.ways.append({"kind": kind, "from": start, "to": end, "points": line,
                          "length": sum(math.dist(a, b) for a, b in zip(line, line[1:])), **fields})
        self.at_node[start].append(index)
        self.at_node[end].append(index)
        return index

    def remove(self, index: int) -> None:
        way = self.ways[index]
        self.ways[index] = None
        self.at_node[way["from"]].remove(index)
        self.at_node[way["to"]].remove(index)

    def degree(self, node: int) -> int:
        return len(self.at_node[node])

    def other(self, way: dict, node: int) -> int:
        return way["to"] if way["from"] == node else way["from"]

    def linked(self, a: int, b: int) -> bool:
        return any(self.other(self.ways[index], a) == b for index in self.at_node[a])

    def outward(self, node: int) -> tuple[float, float]:
        """The way out of a dead end, on past the end of its only walkway."""
        way = self.ways[self.at_node[node][0]]
        points = way["points"]
        inner, end = (points[1], points[0]) if way["from"] == node else (points[-2], points[-1])
        return unit(end[0] - inner[0], end[1] - inner[1])

    def near(self, point, reach: float, among) -> list[tuple[float, int]]:
        """The nodes among those given within reach of a point, nearest first."""
        found = [(math.dist(self.nodes[node], point), node) for node in among]
        return sorted(item for item in found if item[0] <= reach)


def walking_ways(graph: WalkGraph, sources: list[tuple], paved) -> None:
    """A sidewalk walkway down the middle of each footpath, cut where the paving ends and joined where two cross.

    Where two roads' walking lines cross (round a junction's corner, say) the crossing becomes a node; the
    stubs left beyond it, reaching to the kerb, are cut off.
    """
    walk_area = shapely.buffer(paved, -WALK_INSET_M)
    pieces, owners = [], []
    for points, offset, left, fields in sources:
        line = LineString(points).offset_curve(offset if left else -offset, join_style="mitre", mitre_limit=MAX_MITRE)
        for part in shapely.get_parts(shapely.intersection(line, walk_area, grid_size=GRID_M)):
            for piece in shapely.get_parts(shapely.line_merge(part)) if part.geom_type == "MultiLineString" else [part]:
                if piece.geom_type == "LineString" and piece.length > MIN_SEGMENT_M:
                    pieces.append(piece)
                    owners.append(fields)
    if not pieces:
        return
    merged = [line for line in shapely.get_parts(shapely.line_merge(shapely.union_all(pieces, grid_size=GRID_M)))
              if line.geom_type == "LineString"]
    tree = shapely.STRtree(pieces)

    def order(line):
        coords = [tuple(point) for point in shapely.get_coordinates(line)]
        return (min(coords[0], coords[-1]), max(coords[0], coords[-1]), len(coords))

    for line in sorted(merged, key=order):
        # The road whose walking line this mostly is: the one nearest its middle
        middle = line.interpolate(0.5, normalized=True)
        owner = owners[int(min(tree.query_nearest(middle)))]
        graph.add("sidewalk", list(line.coords), **owner)

    for _ in range(3):
        cut = False
        for index, way in enumerate(graph.ways):
            if way is None or way["kind"] != "sidewalk" or way["length"] >= STUB_M:
                continue
            start, end = graph.degree(way["from"]), graph.degree(way["to"])
            if (start == 1 and end >= 3) or (end == 1 and start >= 3):
                graph.remove(index)
                cut = True
        if not cut:
            break


def link_path(a, out_a, b, out_b, area) -> list[tuple[float, float]] | None:
    """A way from one end to another that stays on the paving: straight, or curving round a corner or a dead end."""
    if shapely.covers(area, LineString([a, b])):
        return [a, b]
    reach = math.dist(a, b) * 0.6
    c1 = (a[0] + out_a[0] * reach, a[1] + out_a[1] * reach)
    c2 = (b[0] + out_b[0] * reach, b[1] + out_b[1] * reach)
    curve = []
    for step in range(LINK_CURVE_STEPS + 1):
        t = step / LINK_CURVE_STEPS
        s = 1 - t
        curve.append((s ** 3 * a[0] + 3 * s * s * t * c1[0] + 3 * s * t * t * c2[0] + t ** 3 * b[0],
                      s ** 3 * a[1] + 3 * s * s * t * c1[1] + 3 * s * t * t * c2[1] + t ** 3 * b[1]))
    return curve if shapely.covers(area, LineString(curve)) else None


def width_at(graph: WalkGraph, node: int) -> float:
    widths = [graph.ways[index]["width_m"] for index in graph.at_node[node] if graph.ways[index]["kind"] != "crossing"]
    return min(widths) if widths else LINK_WIDTH_M


def link_ends(graph: WalkGraph, area, ends: list[int], targets: list[int] | None = None, off_road=None) -> list[int]:
    """Join each end to up to LINKS_PER_END of the nearest others (or targets) it can walk to on the paving.

    With off_road (the asphalt and buildings), a straight way that only crosses bare ground will also do: a
    crossing's landing on a central divider or a slip road's island has paving of its own but no walkway.
    """
    targets = ends if targets is None else targets
    made_links = []
    for node in ends:
        if graph.degree(node) == 0:
            continue
        own = set(graph.at_node[node])
        made = 0
        for _, other in graph.near(graph.nodes[node], LINK_REACH_M, targets):
            if made >= LINKS_PER_END:
                break
            if other == node or own & set(graph.at_node[other]):
                continue
            if graph.linked(node, other):
                made += 1
                continue
            out_b = graph.outward(other) if graph.degree(other) == 1 else (0.0, 0.0)
            path = link_path(graph.nodes[node], graph.outward(node), graph.nodes[other], out_b, area)
            if path is None and off_road is not None:
                straight = LineString([graph.nodes[node], graph.nodes[other]])
                path = None if shapely.intersects(off_road, straight) else [graph.nodes[node], graph.nodes[other]]
            if path is None:
                continue
            width = min(width_at(graph, node), width_at(graph, other))
            index = graph.add("link", path, ends=(node, other), width_m=width, lanes=sublanes(width))
            if index is not None:
                made_links.append(index)
            made += 1
    return made_links


def walk_offset(road: dict) -> float:
    """How far from a road's centreline its walking line runs, by its class, sidewalk or not."""
    _, verge, footpath, _ = class_of(road)
    return half_width(road) + KERB_STONE_M + verge + footpath / 2


def uncontrolled_crossing_lines(roads: list[dict], crossing_lines: list[list[list[float]]]) -> list[dict]:
    """A crossing line across each minor road's mouth at a junction, kerb to kerb, where the walking lines go by.

    It sits where the road is first as far from every other road at the junction as their walking lines
    are, so it carries their footpaths straight on over the mouth: across a driveway, the main road's
    footpath; at a crossing of side streets, round each corner. None is made within
    UNCONTROLLED_CLEAR_OF_CROSSING_M of a mapped crossing over the same road.
    """
    lines = {road["id"]: line_of(road) for road in roads}

    def junction(road: dict, end: int):
        # The map's node at the road's end; without one (a hand-made road), the end's own position
        found = road.get("from_node" if end == 0 else "to_node")
        point = lines[road["id"]][0 if end == 0 else -1]
        return found if found is not None else (round(point[0], 2), round(point[1], 2))

    at_node: dict[object, list[dict]] = {}
    for road in roads:
        if len(lines[road["id"]]) >= 2:
            for end in (0, 1):
                at_node.setdefault(junction(road, end), []).append(road)
    mapped = shapely.STRtree([LineString(line) for line in crossing_lines]) if crossing_lines else None
    found = []
    for road in roads:
        points = lines[road["id"]]
        if road["class"] not in UNCONTROLLED_CLASSES or len(points) < 2:
            continue
        for end, ordered in ((0, points), (1, points[::-1])):
            others = [other for other in at_node.get(junction(road, end), ()) if other is not road]
            if len(others) < 2:
                continue
            clear = [(LineString(lines[other["id"]]), walk_offset(other)) for other in others]
            centre = LineString(ordered)
            reach = min(centre.length - 1.0, LINK_REACH_M)
            along = 0.5
            while along < reach and not all(line.distance(centre.interpolate(along)) >= offset for line, offset in clear):
                along += 0.25
            if along >= reach:
                continue
            at = centre.interpolate(along)
            before, after = centre.interpolate(max(along - 0.5, 0.0)), centre.interpolate(min(along + 0.5, centre.length))
            direction = unit(after.x - before.x, after.y - before.y)
            radius = half_width(road)
            line = [[round(at.x - direction[1] * radius, 2), round(at.y + direction[0] * radius, 2)],
                    [round(at.x + direction[1] * radius, 2), round(at.y - direction[0] * radius, 2)]]
            if mapped is not None and any(shapely.intersects(centre, mapped.geometries[near])
                                          for near in mapped.query(LineString(line), predicate="dwithin", distance=UNCONTROLLED_CLEAR_OF_CROSSING_M)):
                continue
            found.append({"id": f"uncontrolled/{road['id']}/{end}", "line_m": line, "control": "uncontrolled"})
    return found


def match_signal(line: list[list[float]], junctions: list[dict]) -> tuple[str, int] | None:
    """The junction and phase whose traffic a signalled crossing cuts across: the nearest approach running across it."""
    a, b = line[0], line[-1]
    centre = ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)
    across = unit(b[0] - a[0], b[1] - a[1])
    best, best_score = None, math.inf
    for junction in junctions:
        for approach in junction.get("approaches", ()):
            distance = math.dist(centre, (approach["stop_x_m"], approach["stop_y_m"]))
            if distance > MATCH_SIGNAL_REACH_M:
                continue
            heading = math.radians(approach["heading_deg"])
            alignment = abs(math.cos(heading) * across[0] + math.sin(heading) * across[1])
            if alignment > MATCH_SIGNAL_MAX_ALIGNMENT:
                continue
            score = distance + alignment * 25
            if score < best_score:
                best, best_score = (junction["id"], int(approach["phase"])), score
    return best


def landing_along(line: list[list[float]], end: int, off_road) -> tuple[tuple[float, float] | None, tuple[float, float] | None]:
    """Where a crossing's end leaves the road, looked for along its line from LTA's end, out past it or back in.

    Returns the kerb (the first spot off the road) and a point half a metre on from it, or Nones.
    """
    a, b = (line[0], line[-1]) if end == 0 else (line[-1], line[0])
    outward = unit(a[0] - b[0], a[1] - b[1])
    if outward == (0.0, 0.0):
        return None, None
    inward_reach = math.dist(a, b) / 2
    steps = []
    for k in range(1, int(RAMP_REACH_M / LANDING_STEP_M) + 1):
        steps.append(k * LANDING_STEP_M)
        if k * LANDING_STEP_M < inward_reach:
            steps.append(-k * LANDING_STEP_M)
    for step in [0.0, *steps]:
        spot = (a[0] + outward[0] * step, a[1] + outward[1] * step)
        on = (spot[0] + outward[0] * 0.5, spot[1] + outward[1] * 0.5) if step >= 0 else spot
        if not shapely.intersects(off_road, Point(spot)) and not shapely.intersects(off_road, Point(on)):
            return (round(spot[0], 2), round(spot[1], 2)), (round(on[0], 2), round(on[1], 2))
    return None, None


def attach_crossings(graph: WalkGraph, crossings: list[dict], kerbs: dict, junctions: list[dict], area, off_road) -> dict:
    """A crossing walkway kerb to kerb along each LTA crossing, each end split into the walkway beside it.

    An end is just up the ramp from its kerb, or a step past LTA's end where no kerb was found (a road this
    area does not draw). It steps onto the nearest walkway it reaches on the paving; failing that, an end off
    the asphalt becomes a node of its own, joined to the walkways it can reach without crossing a road.
    """
    unmatched, unattached, dropped = [], [], []
    lines = [(index, LineString(way["points"])) for index, way in enumerate(graph.ways) if way is not None]
    tree = shapely.STRtree([line for _, line in lines]) if lines else None
    splits: dict[int, list[tuple[float, tuple[float, float]]]] = {}
    planned = []
    for crossing in crossings:
        signal = None
        uncontrolled = crossing.get("control") == "uncontrolled"
        if crossing.get("control") == "signal":
            signal = match_signal(crossing["line_m"], junctions)
            if signal is None:
                unmatched.append(crossing["id"])
                continue
        ends = []
        for end in (0, 1):
            found = kerbs.get(f"{crossing['id']}/{end}")
            kerb, outward = found if found is not None else (None, None)
            # Just up the ramp from the kerb, where the crossing's walker is on the sidewalk
            anchor = None if kerb is None else (round(kerb[0] + outward[0] * 0.5, 2), round(kerb[1] + outward[1] * 0.5, 2))
            if anchor is None or shapely.intersects(off_road, Point(anchor)):
                # No kerb, or one onto more asphalt (a junction's mouth): the nearest spot off the road along the line
                kerb, anchor = landing_along(crossing["line_m"], end, off_road)
                if anchor is None:
                    break
            target = None
            candidates = tree.query(Point(anchor), predicate="dwithin", distance=CROSSING_REACH_M) if tree else []
            options = sorted((lines[k][1].distance(Point(anchor)), lines[k][0]) for k in candidates)
            for _, way_index in options:
                line = LineString(graph.ways[way_index]["points"])
                along = line.project(Point(anchor))
                onto = line.interpolate(along)
                if shapely.covers(area, LineString([anchor, (onto.x, onto.y)])):
                    target = ("split", way_index, along, (onto.x, onto.y))
                    break
            if target is None and not uncontrolled:
                # A landing with no walkway on it (a central divider, say): the crossing ends here
                target = ("free", -1, 0.0, anchor)
            if target is None:
                break
            ends.append((kerb, target))
        if len(ends) < 2:
            (dropped if uncontrolled else unattached).append(crossing["id"])
            continue
        for _, (how, way_index, along, point) in ends:
            if how == "split":
                splits.setdefault(way_index, []).append((along, point))
        planned.append((crossing, signal, ends))

    # Each walkway split at every point a crossing steps onto it, into one walkway between each two
    split_nodes: dict[tuple[float, float], int] = {}
    for way_index in sorted(splits):
        way = graph.ways[way_index]
        line = LineString(way["points"])
        cuts = [(0.0, way["from"])]
        for along, point in sorted(splits[way_index]):
            node = graph.node(point)
            split_nodes[point] = node
            cuts.append((along, node))
        cuts.append((line.length, way["to"]))
        fields = {key: value for key, value in way.items() if key not in ("kind", "from", "to", "points", "length")}
        graph.remove(way_index)
        kept = [cuts[0]]
        for along, node in cuts[1:]:
            if node != kept[-1][1]:
                kept.append((along, node))
        for (start_along, start), (end_along, end) in zip(kept, kept[1:]):
            piece = shapely.ops.substring(line, start_along, end_along)
            points = list(piece.coords) if piece.geom_type == "LineString" else [graph.nodes[start], graph.nodes[end]]
            graph.add(way["kind"], points, ends=(start, end), **fields)

    attached = []
    for crossing, signal, ends in planned:
        nodes = []
        for _, (how, _, _, point) in ends:
            nodes.append(split_nodes[point] if how == "split" else graph.node(point))
        (kerb_a, _), (kerb_b, _) = ends
        control = "signal" if signal else "uncontrolled" if crossing.get("control") == "uncontrolled" else "zebra"
        fields = {"crossing_id": crossing["id"], "control": control}
        if signal:
            fields.update(junction_id=signal[0], phase=signal[1])
        index = graph.add("crossing", [graph.nodes[nodes[0]], kerb_a, kerb_b, graph.nodes[nodes[1]]], ends=(nodes[0], nodes[1]),
                          width_m=RAMP_WIDTH_M, lanes=sublanes(RAMP_WIDTH_M), **fields)
        if index is None:
            (dropped if control == "uncontrolled" else unattached).append(crossing["id"])
        else:
            attached.append(index)

    # A crossing's end on a landing joins the walkways near it; one that cannot reach any leads nowhere
    landing_ends = sorted({node for index in attached for node in (graph.ways[index]["from"], graph.ways[index]["to"])
                           if graph.degree(node) == 1})
    walk_nodes = [node for node in range(len(graph.nodes)) if graph.degree(node) > 0]
    landing_links = link_ends(graph, area, landing_ends, walk_nodes, off_road)
    for index in attached:
        way = graph.ways[index]
        if graph.degree(way["from"]) == 1 or graph.degree(way["to"]) == 1:
            graph.remove(index)
            (dropped if way["control"] == "uncontrolled" else unattached).append(way["crossing_id"])
    # A landing whose crossing went keeps no links: they would lead to nothing
    landings = set(landing_ends)
    for index in landing_links:
        way = graph.ways[index]
        if way is not None and any(node in landings and not any(graph.ways[other]["kind"] == "crossing" for other in graph.at_node[node])
                                   for node in (way["from"], way["to"])):
            graph.remove(index)
    return {"unmatched": sorted(unmatched), "unattached": sorted(unattached), "dropped": len(dropped)}


def walk_network(graph: WalkGraph) -> tuple[list[dict], list[dict], list[dict], dict]:
    """The graph's nodes, walkways and walker seeds as the file holds them, and how well it holds together."""
    used = sorted({node for way in graph.ways if way is not None for node in (way["from"], way["to"])})
    number = {node: index for index, node in enumerate(used)}
    ways = [way for way in graph.ways if way is not None]
    degree = [0] * len(used)
    for way in ways:
        degree[number[way["from"]]] += 1
        degree[number[way["to"]]] += 1
    crossing_ends = {number[node] for way in ways if way["kind"] == "crossing" for node in (way["from"], way["to"])}
    nodes = [{"id": index, "xy_m": list(graph.nodes[node]),
              "kind": "kerb" if index in crossing_ends else "corner" if degree[index] >= 3 else "joint"}
             for index, node in enumerate(used)]

    walkways = []
    for index, way in enumerate(ways):
        item = {"id": index, "kind": way["kind"], "from": number[way["from"]], "to": number[way["to"]],
                "points_m": [list(point) for point in way["points"]], "length_m": round(way["length"], 2),
                "width_m": way["width_m"], "lanes": way["lanes"]}
        for key in ("road_id", "side", "crossing_id", "control", "junction_id", "phase"):
            if key in way:
                item[key] = way[key]
        walkways.append(item)

    # Connected parts, by walkway length
    parent = list(range(len(nodes)))

    def root(node):
        while parent[node] != node:
            parent[node] = parent[parent[node]]
            node = parent[node]
        return node

    for way in walkways:
        a, b = root(way["from"]), root(way["to"])
        if a != b:
            parent[max(a, b)] = min(a, b)
    lengths: dict[int, float] = {}
    for way in walkways:
        lengths[root(way["from"])] = lengths.get(root(way["from"]), 0.0) + way["length_m"]
    total = sum(lengths.values())
    main = min(lengths, key=lambda part: (-lengths[part], part)) if lengths else -1

    # Walkers start spread evenly by length along the main part's sidewalks, never on a crossing
    rng = random.Random(SEED)
    homes = [way for way in walkways if way["kind"] != "crossing" and root(way["from"]) == main]
    starts, along = [], 0.0
    for way in homes:
        starts.append(along)
        along += way["length_m"]
    seeds = []
    for count in range(SEED_COUNT if homes else 0):
        at = rng.random() * along
        home = bisect.bisect_right(starts, at) - 1
        way = homes[home]
        seeds.append({"id": f"ped-{count + 1:04d}", "walkway": way["id"],
                      "distance_m": round(min(at - starts[home], way["length_m"]), 2),
                      "speed_mps": round(rng.uniform(*SEED_SPEED_MPS), 2), "height_m": round(rng.uniform(*SEED_HEIGHT_M), 2),
                      "model": rng.randrange(SEED_MODELS)})
    report = {"walk_nodes": len(nodes), "walkways": len(walkways),
              "sidewalk_walkways": sum(way["kind"] == "sidewalk" for way in walkways),
              "link_walkways": sum(way["kind"] == "link" for way in walkways),
              "crossing_walkways": sum(way["kind"] == "crossing" for way in walkways),
              "walkway_length_m": round(total, 1), "components": len(lengths),
              "main_component_share": round(lengths[main] / total, 4) if total else 0.0, "seeds": len(seeds)}
    return nodes, walkways, seeds, report


def build(roads: list[dict], tags_by_way: dict[int, dict], crossings: list[dict], buildings: list[dict], area_key: str,
          junctions: list[dict] | None = None) -> dict:
    """The walkways file's contents for an area. Junctions are the signals file's, for signalled crossings."""
    roads = sorted(roads, key=lambda road: road["id"])
    asphalt_parts, band_parts, verge_parts = [], [], []
    walk_sources = []
    sides_report = {"left": 0, "right": 0, "none": 0}
    for road in roads:
        points = line_of(road)
        if len(points) < 2:
            continue
        radius = half_width(road)
        asphalt_parts.extend(carriageway(points, radius))
        tags = tags_by_way.get(int(road["id"].split("/")[1]), {})
        left, right = sidewalk_sides(road, tags)
        _, verge, footpath, lanes = class_of(road)
        sides_report["left"] += left
        sides_report["right"] += right
        sides_report["none"] += not left and not right
        for has, is_left in ((left, True), (right, False)):
            if not has:
                continue
            band_parts.extend(side_band(points, radius + KERB_STONE_M + verge + footpath, is_left))
            walk_sources.append((points, radius + KERB_STONE_M + verge + footpath / 2, is_left,
                                 {"width_m": footpath, "lanes": lanes, "road_id": road["id"], "side": "left" if is_left else "right"}))
            if verge > 0:
                verge_parts.extend(side_band(points, radius + KERB_STONE_M + verge, is_left))
    roads_area = union(asphalt_parts)
    roads_edge = roads_area.boundary
    building_area = union([drop_small(shapely.make_valid(Polygon(ring)), 0) for building in sorted(buildings, key=lambda b: b["id"])
                           for ring in building["rings_m"] if len(ring) >= 4])
    shapely.prepare(roads_area)
    blocked = shapely.union(roads_area, building_area, grid_size=GRID_M)

    # Where each crossing meets a kerb. A crossing whose end has no road near it is on a road this area
    # does not import (an expressway, say), and has nothing to land on.
    kerbs: list[tuple[str, tuple[tuple[float, float], tuple[float, float]] | None]] = []
    for crossing in sorted(crossings, key=lambda crossing: crossing["id"]):
        for end in (0, 1):
            kerbs.append((f"{crossing['id']}/{end}", kerb_point(crossing["line_m"], end, roads_area, roads_edge)))
    # Crossings with no light over minor roads' mouths, where the footpaths carry on across them. They get
    # ramps where they meet a sidewalk, but no landings: one that has no sidewalk to land on is not made.
    uncontrolled = uncontrolled_crossing_lines(roads, [crossing["line_m"] for crossing in crossings])
    side_kerbs = [(f"{crossing['id']}/{end}", kerb_point(crossing["line_m"], end, roads_area, roads_edge))
                  for crossing in uncontrolled for end in (0, 1)]

    paved = shapely.difference(union(band_parts), blocked, grid_size=GRID_M)
    # A crossing always lands on something to stand on: where its kerb has no sidewalk (a central divider,
    # or a side OpenStreetMap leaves bare) it gets a landing as wide as the crossing and as deep as its ramp
    landings = [landing_shape(*found) for _, found in kerbs if found is not None
                and shapely.intersection(ramp_shape(*found), paved).area < MIN_RAMP_M2]
    if landings:
        paved = shapely.difference(union([*band_parts, *landings]), blocked, grid_size=GRID_M)
    paved = shapely.simplify(paved, SIMPLIFY_M)
    paved = drop_small(shapely.difference(drop_small(shapely.make_valid(paved), 0), roads_area, grid_size=GRID_M), MIN_PIECE_M2)
    shapely.prepare(paved)

    # Ramps where a crossing meets a sidewalk, each kept clear of those before it
    ramps: list[list[int]] = []
    ramp_areas: list[Polygon] = []
    ramp_owner: list[int] = []
    unramped, off_road = [], []
    taken_parts: list[Polygon] = []
    taken_grid: dict[tuple[int, int], list[int]] = {}

    def cells(geometry):
        x0, y0, x1, y1 = geometry.bounds
        return [(x, y) for x in range(math.floor(x0 / 10), math.floor(x1 / 10) + 1)
                for y in range(math.floor(y0 / 10), math.floor(y1 / 10) + 1)]

    mapped_ramps = 0
    for number_of_kerb, (name, found) in enumerate([*kerbs, *side_kerbs]):
        mapped = number_of_kerb < len(kerbs)
        if found is None:
            if mapped:
                off_road.append(name)
            continue
        kerb, outward = found
        area = drop_small(shapely.intersection(ramp_shape(kerb, outward), paved, grid_size=GRID_M), 0.05)
        # Clear of the ramps already made, of which only those nearby can touch it
        nearby = sorted({index for cell in cells(area) for index in taken_grid.get(cell, ())}) if not area.is_empty else []
        if nearby:
            area = drop_small(shapely.difference(area, union([taken_parts[index] for index in nearby]), grid_size=GRID_M), 0.05)
        if area.is_empty or area.area < MIN_RAMP_M2:
            if mapped:
                unramped.append(name)
            continue
        mapped_ramps += mapped
        number = len(ramps)
        ramps.append([cm(kerb[0]), cm(kerb[1]), int(round(outward[0] * 10000)), int(round(outward[1] * 10000))])
        for piece in polygons(area):
            ramp_areas.append(piece)
            ramp_owner.append(number)
        for piece in polygons(area):
            for cell in cells(piece):
                taken_grid.setdefault(cell, []).append(len(taken_parts))
            taken_parts.append(piece)
    taken = union(taken_parts) if taken_parts else Polygon()

    flat = drop_small(shapely.difference(paved, taken, grid_size=GRID_M), 0) if not taken.is_empty else paved
    kerb_band = shapely.buffer(roads_area, KERB_STONE_M, join_style="mitre", mitre_limit=MAX_MITRE)
    verge_band = union(verge_parts) if verge_parts else Polygon()
    kerb_zone = drop_small(shapely.intersection(flat, kerb_band, grid_size=GRID_M), 0)
    rest = drop_small(shapely.difference(flat, kerb_band, grid_size=GRID_M), 0)
    verge_zone = drop_small(shapely.intersection(rest, verge_band, grid_size=GRID_M), 0) if not verge_band.is_empty else Polygon()
    footpath_zone = drop_small(shapely.difference(rest, verge_band, grid_size=GRID_M), 0) if not verge_band.is_empty else rest

    # Every piece of every zone, cut into triangles and filed under its tile
    tiles: dict[tuple[int, int], Tile] = {}

    def tile_for(x_cm: float, y_cm: float) -> Tile:
        key = tile_key(x_cm, y_cm)
        if key not in tiles:
            tiles[key] = Tile(key)
        return tiles[key]

    pieces: list[tuple[str, int, Polygon]] = []
    for zone, geometry in (("footpath", footpath_zone), ("kerb_stone", kerb_zone), ("verge", verge_zone)):
        for piece in polygons(geometry):
            if piece.area > 1e-4:
                pieces.append((zone, -1, piece))
    for piece, owner in zip(ramp_areas, ramp_owner):
        pieces.append(("ramp", owner, piece))

    triangle_count = 0
    for zone, owner, piece in pieces:
        for a, b, c in triangles_cm(piece):
            tile = tile_for((a[0] + b[0] + c[0]) / 3, (a[1] + b[1] + c[1]) / 3)
            corners = [tile.vertex(a), tile.vertex(b), tile.vertex(c)]
            if zone == "ramp":
                tile.ramp_triangles.extend([*corners, tile.ramp(owner, ramps)])
            else:
                tile.zones[zone].extend(corners)
            triangle_count += 1

    # Edges: a piece's edge that no other piece shares is the sidewalk's edge, at the kerb or facing the
    # ground; one a ramp shares with the flat sidewalk is the ramp's side (cheek)
    uses: dict[tuple[tuple[int, int], tuple[int, int]], list[tuple[int, bool]]] = {}
    for zone, owner, piece in pieces:
        for ring in rings_cm(piece):
            for a, b in zip(ring, ring[1:]):
                key = (a, b) if a <= b else (b, a)
                uses.setdefault(key, []).append((owner, a <= b))
    edges = []
    for (a, b), users in sorted(uses.items()):
        ramp_users = [user for user in users if user[0] >= 0]
        if len(users) == 1:
            owner, forward = users[0]
            # The sidewalk is on the left of its edge, so the face looks out to the right of start->end
            start, end = (a, b) if forward else (b, a)
            edges.append(("edge", owner, start, end))
        elif len(ramp_users) == 1 and len(users) == 2:
            owner, forward = ramp_users[0]
            # The ramp is on the left of its own edge; its cheek looks into it, so the edge is turned round
            start, end = (b, a) if forward else (a, b)
            edges.append(("cheek_edges", owner, start, end))
    middles = shapely.points([((s[0] + e[0]) / 200, (s[1] + e[1]) / 200) for _, _, s, e in edges]) if edges else []
    at_kerb = shapely.dwithin(roads_area, middles, KERB_EDGE_M) if edges else []
    kerb_count = outer_count = cheek_count = 0
    for (kind, owner, start, end), kerb in zip(edges, at_kerb):
        if kind == "edge":
            kind = "kerb_edges" if kerb else "outer_edges"
        kerb_count += kind == "kerb_edges"
        outer_count += kind == "outer_edges"
        cheek_count += kind == "cheek_edges"
        tile = tile_for((start[0] + end[0]) / 2, (start[1] + end[1]) / 2)
        ramp = tile.ramp(owner, ramps) if owner >= 0 else -1
        tile.edges[kind].extend([tile.vertex(start), tile.vertex(end), ramp])

    # The walk network, on the paving just drawn
    walkable = shapely.buffer(paved, 0.05)
    shapely.prepare(walkable)
    graph = WalkGraph()
    walking_ways(graph, walk_sources, paved)
    dead_ends = [node for node in range(len(graph.nodes)) if graph.degree(node) == 1]
    link_ends(graph, walkable, dead_ends)
    # Off the road: the asphalt, less a little at its edge so a way along the kerb still counts, and buildings
    obstacles = shapely.union(shapely.buffer(roads_area, -0.1), building_area, grid_size=GRID_M)
    shapely.prepare(obstacles)
    crossing_report = attach_crossings(graph, [*sorted(crossings, key=lambda crossing: crossing["id"]), *uncontrolled],
                                       dict([*kerbs, *side_kerbs]), junctions or [], walkable, obstacles)
    nodes, walkways, seeds, walk_report = walk_network(graph)

    partly_ramped = {name.split("/")[0] for name in unramped + off_road}
    return {
        "area": area_key,
        "version": VERSION,
        "constants": {"lane_width_m": LANE_WIDTH_M, "shoulder_m": SHOULDER_M, "surface_y": SURFACE_Y,
                      "kerb_rise_m": KERB_RISE_M, "kerb_stone_m": KERB_STONE_M, "ramp_depth_m": RAMP_DEPTH_M,
                      "ramp_width_m": RAMP_WIDTH_M, "tile_m": TILE_M,
                      "classes": {name: {"default_sides": sides, "verge_m": verge, "footpath_m": footpath, "lanes": lanes}
                                  for name, (sides, verge, footpath, lanes) in sorted(CLASSES.items())}},
        "render": {"tiles": [tiles[key].json() for key in sorted(tiles)]},
        "nodes": nodes,
        "walkways": walkways,
        "seeds": seeds,
        "report": {"roads": len(roads), "road_sides_left": sides_report["left"], "road_sides_right": sides_report["right"],
                   "roads_without_sidewalk": sides_report["none"], "paved_area_m2": round(paved.area, 1),
                   "triangles": triangle_count, "kerb_edges": kerb_count, "outer_edges": outer_count,
                   "cheek_edges": cheek_count, "ramps": mapped_ramps, "uncontrolled_ramps": len(ramps) - mapped_ramps,
                   "crossings": len(crossings),
                   "crossings_with_both_ramps": len(crossings) - len(partly_ramped), "landings": len(landings),
                   "unramped_crossing_ends": unramped, "crossing_ends_off_roads": off_road,
                   **walk_report,
                   "uncontrolled_crossings": sum(way["control"] == "uncontrolled" for way in walkways if way["kind"] == "crossing"),
                   "uncontrolled_not_made": crossing_report["dropped"],
                   "unattached_crossings": len(crossing_report["unattached"]),
                   "unmatched_signal_crossings": len(crossing_report["unmatched"]),
                   "unattached_crossing_ids": crossing_report["unattached"],
                   "unmatched_signal_crossing_ids": crossing_report["unmatched"]},
    }


def source_hash(*inputs) -> str:
    """A hash of everything the file is made from, and of this importer's settings, so a stale file shows."""
    digest = hashlib.sha256()
    for item in inputs:
        digest.update(json.dumps(item, sort_keys=True, separators=(",", ":")).encode("utf-8"))
    settings = {"version": VERSION, "lane": LANE_WIDTH_M, "shoulder": SHOULDER_M, "rise": KERB_RISE_M, "stone": KERB_STONE_M,
                "ramp": [RAMP_DEPTH_M, RAMP_WIDTH_M, RAMP_REACH_M], "classes": CLASSES, "simplify": SIMPLIFY_M,
                "walk": [WALK_INSET_M, MERGE_NODE_M, STUB_M, LINK_REACH_M, LINKS_PER_END, CROSSING_REACH_M,
                         MATCH_SIGNAL_REACH_M, MATCH_SIGNAL_MAX_ALIGNMENT, SUBLANE_M],
                "uncontrolled": [sorted(UNCONTROLLED_CLASSES), UNCONTROLLED_CLEAR_OF_CROSSING_M],
                "seeds": [SEED_COUNT, SEED, SEED_SPEED_MPS, SEED_HEIGHT_M, SEED_MODELS]}
    digest.update(json.dumps(settings, sort_keys=True).encode("utf-8"))
    return digest.hexdigest()[:16]


def sidewalk_tags(osm: dict) -> dict[int, dict]:
    """Each way's tags that bear on its sidewalks."""
    return {element["id"]: {key: value for key, value in element.get("tags", {}).items()
                            if key.startswith("sidewalk") or key == "oneway"}
            for element in osm["elements"] if element["type"] == "way"}


def inputs(area: str) -> tuple[list[dict], dict[int, dict], list[dict], list[dict]]:
    prepared = json.loads((OUTPUT_DIRECTORY / f"{area}.json").read_text(encoding="utf-8"))
    osm = json.loads(extract_path(area).read_text(encoding="utf-8"))
    crossings = json.loads(output_path(area, "crossings").read_text(encoding="utf-8"))["crossings"]
    buildings = read_buildings(BUILDINGS_DIRECTORY / f"{area}.sqlite")
    return prepared["roads"], sidewalk_tags(osm), crossings, buildings


def signal_junctions(area: str) -> list[dict]:
    """The signals file's junctions, with only what matching a crossing to its light needs."""
    path = output_path(area, "signals")
    if not path.is_file():
        return []
    return [{"id": junction["id"], "approaches": [{key: approach[key] for key in ("heading_deg", "stop_x_m", "stop_y_m", "phase")}
                                                  for approach in junction.get("approaches", ())]}
            for junction in json.loads(path.read_text(encoding="utf-8"))["junctions"]]


def check_network(data: dict) -> None:
    """A network in pieces means a width or tag rule broke it: refuse to write it."""
    report = data["report"]
    if report["main_component_share"] < MIN_MAIN_SHARE:
        raise ValueError(f"the walk network's largest connected part holds only {report['main_component_share']:.0%} of its "
                         f"length ({report['components']} parts); at least {MIN_MAIN_SHARE:.0%} is needed")


def build_area(area: str) -> dict:
    roads, tags, crossings, buildings = inputs(area)
    crossing_lines = [{"id": crossing["id"], "line_m": crossing["line_m"], "control": crossing.get("control", "no_signal")}
                      for crossing in crossings]
    rings = [{"id": building["id"], "rings_m": building["rings_m"]} for building in buildings]
    junctions = signal_junctions(area)
    data = build(roads, tags, crossing_lines, rings, area, junctions)
    check_network(data)
    used_tags = {str(key): value for key, value in sorted(tags.items()) if value}
    return {"area": data.pop("area"), "version": data.pop("version"),
            "source_hash": source_hash(roads, used_tags, crossing_lines, rings, junctions),
            "attribution": [ROADS_ATTRIBUTION, "Crossings and signals from LTA DataMall static datasets, under the Singapore "
                            "Open Data Licence version 1.0. Sidewalk widths and heights are chosen, not measured."],
            **data}


def write(path: Path, data: dict) -> None:
    """Compact, with one tile, node, walkway or seed per line, so a rebuild's diff shows what changed."""
    parts = []
    for key, value in data.items():
        if key == "render":
            tiles = ",\n".join("  " + json.dumps(tile, separators=(",", ":")) for tile in value["tiles"])
            parts.append(f'"render": {{"tiles": [\n{tiles}\n ]}}')
        elif key in ("nodes", "walkways", "seeds") and value:
            items = ",\n".join("  " + json.dumps(item, separators=(",", ":")) for item in value)
            parts.append(f"{json.dumps(key)}: [\n{items}\n ]")
        else:
            parts.append(f"{json.dumps(key)}: {json.dumps(value, separators=(',', ':'))}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("{\n " + ",\n ".join(parts) + "\n}\n", encoding="utf-8", newline="\n")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--area", choices=sorted(SERVICE_AREAS), default=DEFAULT_AREA)
    parser.add_argument("--output", type=Path, default=None, help="JSON file to write")
    parser.add_argument("--unity-copy", type=Path, default=None,
                        help="also write the file here, for the view's offline fallback")
    arguments = parser.parse_args(argv)
    try:
        data = build_area(arguments.area)
    except (OSError, KeyError, ValueError) as error:
        print(f"Could not build the walkways; nothing was written. {error}", file=sys.stderr)
        return 1
    output = arguments.output or output_path(arguments.area, "walkways")
    write(output, data)
    if arguments.unity_copy:
        write(arguments.unity_copy, data)
    report = data["report"]
    print(f"{arguments.area}: {report['paved_area_m2']:.0f} square metres of sidewalk, {report['triangles']} triangles in "
          f"{len(data['render']['tiles'])} tiles, {report['ramps']} ramps; {report['crossings_with_both_ramps']} of "
          f"{report['crossings']} crossings ramped at both ends. Walk network: {report['walkways']} walkways "
          f"({report['crossing_walkways']} crossings, {report['uncontrolled_crossings']} of them without a light), "
          f"{report['main_component_share']:.0%} of it joined up, {report['seeds']} walkers; {report['unattached_crossings']} "
          f"crossings with nowhere to land, {report['unmatched_signal_crossings']} whose light cannot be told. Wrote {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
