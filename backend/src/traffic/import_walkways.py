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

This is phase 1 of the sidewalks plan (sidewalks_plan.md): geometry only. The walk network comes later.
Rebuild the Unity copy with --unity-copy ../Assets/Formula9/Resources/Formula9/RoadData/<area>-walkways.json.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
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

VERSION = 1

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


def build(roads: list[dict], tags_by_way: dict[int, dict], crossings: list[dict], buildings: list[dict], area_key: str) -> dict:
    """The walkways file's contents for an area."""
    roads = sorted(roads, key=lambda road: road["id"])
    asphalt_parts, band_parts, verge_parts = [], [], []
    sides_report = {"left": 0, "right": 0, "none": 0}
    for road in roads:
        points = line_of(road)
        if len(points) < 2:
            continue
        radius = half_width(road)
        asphalt_parts.extend(carriageway(points, radius))
        tags = tags_by_way.get(int(road["id"].split("/")[1]), {})
        left, right = sidewalk_sides(road, tags)
        _, verge, footpath, _ = class_of(road)
        sides_report["left"] += left
        sides_report["right"] += right
        sides_report["none"] += not left and not right
        for has, is_left in ((left, True), (right, False)):
            if not has:
                continue
            band_parts.extend(side_band(points, radius + KERB_STONE_M + verge + footpath, is_left))
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
    taken = Polygon()
    for name, found in kerbs:
        if found is None:
            off_road.append(name)
            continue
        kerb, outward = found
        area = drop_small(shapely.intersection(ramp_shape(kerb, outward), paved, grid_size=GRID_M), 0.05)
        area = drop_small(shapely.difference(area, taken, grid_size=GRID_M), 0.05)
        if area.is_empty or area.area < MIN_RAMP_M2:
            unramped.append(name)
            continue
        number = len(ramps)
        ramps.append([cm(kerb[0]), cm(kerb[1]), int(round(outward[0] * 10000)), int(round(outward[1] * 10000))])
        for piece in polygons(area):
            ramp_areas.append(piece)
            ramp_owner.append(number)
        taken = drop_small(shapely.union(taken, area, grid_size=GRID_M), 0)

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
        "report": {"roads": len(roads), "road_sides_left": sides_report["left"], "road_sides_right": sides_report["right"],
                   "roads_without_sidewalk": sides_report["none"], "paved_area_m2": round(paved.area, 1),
                   "triangles": triangle_count, "kerb_edges": kerb_count, "outer_edges": outer_count,
                   "cheek_edges": cheek_count, "ramps": len(ramps), "crossings": len(crossings),
                   "crossings_with_both_ramps": len(crossings) - len(partly_ramped), "landings": len(landings),
                   "unramped_crossing_ends": unramped, "crossing_ends_off_roads": off_road},
    }


def source_hash(*inputs) -> str:
    """A hash of everything the file is made from, and of this importer's settings, so a stale file shows."""
    digest = hashlib.sha256()
    for item in inputs:
        digest.update(json.dumps(item, sort_keys=True, separators=(",", ":")).encode("utf-8"))
    settings = {"version": VERSION, "lane": LANE_WIDTH_M, "shoulder": SHOULDER_M, "rise": KERB_RISE_M, "stone": KERB_STONE_M,
                "ramp": [RAMP_DEPTH_M, RAMP_WIDTH_M, RAMP_REACH_M], "classes": CLASSES, "simplify": SIMPLIFY_M}
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


def build_area(area: str) -> dict:
    roads, tags, crossings, buildings = inputs(area)
    crossing_lines = [{"id": crossing["id"], "line_m": crossing["line_m"]} for crossing in crossings]
    rings = [{"id": building["id"], "rings_m": building["rings_m"]} for building in buildings]
    data = build(roads, tags, crossing_lines, rings, area)
    used_tags = {str(key): value for key, value in sorted(tags.items()) if value}
    return {"area": data.pop("area"), "version": data.pop("version"),
            "source_hash": source_hash(roads, used_tags, crossing_lines, rings),
            "attribution": [ROADS_ATTRIBUTION, "Crossings from LTA DataMall static datasets, under the Singapore Open Data "
                            "Licence version 1.0. Sidewalk widths and heights are chosen, not measured."],
            **data}


def write(path: Path, data: dict) -> None:
    """Compact, with one tile per line, so a rebuild's diff shows which squares changed."""
    parts = []
    for key, value in data.items():
        if key == "render":
            tiles = ",\n".join("  " + json.dumps(tile, separators=(",", ":")) for tile in value["tiles"])
            parts.append(f'"render": {{"tiles": [\n{tiles}\n ]}}')
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
          f"{report['crossings']} crossings ramped at both ends. Wrote {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
