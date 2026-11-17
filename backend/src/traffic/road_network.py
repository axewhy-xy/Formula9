"""The roads the background traffic drives on, as the 3D city builds them.

Every road of an area's prepared traffic file (src.traffic.prepared) becomes one directed road a way
along it, two for a two-way road, with its lanes, speed limit and the stop lines of the lights that
control it (src.traffic.route_signals reads the signal file). This is the network the Unity client's
CityTrafficRenderer builds from the same files, so a vehicle at a road, lane and distance here is at the
same place there. Nothing here changes once built, so every session shares one network per area.

Points are planar metres, x east and y north, as in the prepared files. A direction is a unit vector;
its left is (-y, x), which is where Singapore's kerb is.
"""

from __future__ import annotations

import bisect
import math
from functools import lru_cache

from src.traffic import route_signals
from src.traffic.prepared import prepared_area

LANE_WIDTH_M = 3.2
# A junction movement is compared along this much road either side of the junction, and two that come
# this close conflict
MOVEMENT_REACH_M = 6.0
CONFLICT_M = 2.2
# A road with a light outranks one without, as a junction picks who goes first
SIGNAL_RANK = 10.0
# The spatial index's cell, and how close a road must be to a point to be the road there
CELL_M = 40.0
MATCH_M = 5.0
# A light's stop line belongs to the way along its road that runs with its approach
STOP_ALIGN = 0.5

_CLASS_WEIGHT = {"primary": 8.0, "primary_link": 4.0, "secondary": 5.0, "secondary_link": 3.0, "tertiary": 3.0,
                 "tertiary_link": 2.0, "living_street": 0.5, "service": 0.5}
_CLASS_RANK = {"primary": 6.0, "primary_link": 5.0, "secondary": 4.0, "secondary_link": 3.0, "tertiary": 3.0,
               "tertiary_link": 2.0, "service": 0.0}


def class_weight(road_class: str) -> float:
    """How readily traffic turns onto a road of this class."""
    return _CLASS_WEIGHT.get(road_class, 1.0)


def class_rank(road_class: str) -> float:
    """How much right of way a road of this class has at a junction."""
    return _CLASS_RANK.get(road_class, 1.0)


def inverse_lerp(a: float, b: float, value: float) -> float:
    return min(1.0, max(0.0, (value - a) / (b - a))) if a != b else 0.0


def lerp(a: float, b: float, t: float) -> float:
    return a + (b - a) * min(1.0, max(0.0, t))


def angle_deg(ax: float, ay: float, bx: float, by: float) -> float:
    """The angle between two directions, in degrees."""
    scale = math.sqrt((ax * ax + ay * ay) * (bx * bx + by * by))
    if scale < 1e-15:
        return 0.0
    return math.degrees(math.acos(min(1.0, max(-1.0, (ax * bx + ay * by) / scale))))


def _unit(x: float, y: float, fallback: tuple[float, float] = (0.0, 1.0)) -> tuple[float, float]:
    length = math.hypot(x, y)
    # As Unity's normalized, which leaves a vector this short as zero
    if length * length < 0.001:
        return fallback
    return x / length, y / length


def point_to_segment(px: float, py: float, ax: float, ay: float, bx: float, by: float) -> float:
    dx, dy = bx - ax, by - ay
    length_sq = dx * dx + dy * dy
    share = min(1.0, max(0.0, ((px - ax) * dx + (py - ay) * dy) / length_sq)) if length_sq > 0 else 0.0
    return math.hypot(px - ax - dx * share, py - ay - dy * share)


def segment_distance(a1, a2, b1, b2) -> float:
    """The closest two segments come, zero where they cross."""
    def side(p, q, r):
        return (q[0] - p[0]) * (r[1] - p[1]) - (q[1] - p[1]) * (r[0] - p[0])
    if side(a1, a2, b1) * side(a1, a2, b2) < 0 and side(b1, b2, a1) * side(b1, b2, a2) < 0:
        return 0.0
    return min(point_to_segment(*a1, *b1, *b2), point_to_segment(*a2, *b1, *b2),
               point_to_segment(*b1, *a1, *a2), point_to_segment(*b2, *a1, *a2))


class Road:
    """One way along one road of the area."""

    __slots__ = ("index", "key", "road_id", "cls", "lanes", "speed", "oneway", "from_node", "to_node",
                 "xs", "ys", "along", "length", "sx", "sy", "ex", "ey", "stops", "rank", "service")

    def __init__(self, index: int, source: dict, direction: int, points: list[tuple[float, float]]):
        self.index = index
        self.road_id = source["id"]
        self.key = f"{source['id']}|{direction}"
        self.cls = source.get("class") or ""
        self.service = self.cls == "service"
        self.lanes = max(1, int(source.get("lanes") or 1))
        self.speed = max(2.0, float(source.get("speed_kph") or 40) / 3.6)
        self.oneway = bool(source.get("oneway"))
        self.from_node = source["from_node"] if direction == 1 else source["to_node"]
        self.to_node = source["to_node"] if direction == 1 else source["from_node"]
        self.xs = [p[0] for p in points]
        self.ys = [p[1] for p in points]
        along = [0.0]
        for (ax, ay), (bx, by) in zip(points, points[1:]):
            along.append(along[-1] + math.hypot(bx - ax, by - ay))
        self.along = along
        self.length = along[-1]
        self.sx, self.sy = _unit(points[1][0] - points[0][0], points[1][1] - points[0][1])
        self.ex, self.ey = _unit(points[-1][0] - points[-2][0], points[-1][1] - points[-2][1])
        # (metres along, junction offset, phase, junction id) of each light's stop line on it
        self.stops: list[tuple[float, int, int, str]] = []
        self.rank = class_rank(self.cls)

    def lane_offset(self, lane: int) -> float:
        """Metres to the left of travel of a lane's middle, lane 0 at the kerb."""
        lane = min(max(lane, 0), self.lanes - 1)
        return (lane - (self.lanes - 1) * 0.5) * LANE_WIDTH_M if self.oneway else (lane + 0.5) * LANE_WIDTH_M

    def lane_from(self, lane: int) -> int:
        return min(max(lane, 0), self.lanes - 1)

    def sample(self, distance: float, left: float) -> tuple[float, float, float, float]:
        """The point this far along and this far to the left, and the way the road runs there."""
        along = self.along
        d = min(max(distance, 0.0), self.length)
        index = min(max(bisect.bisect_left(along, d), 1), len(along) - 1)
        ax, ay = self.xs[index - 1], self.ys[index - 1]
        bx, by = self.xs[index], self.ys[index]
        segment = max(0.001, along[index] - along[index - 1])
        t = min(1.0, max(0.0, (d - along[index - 1]) / segment))
        tx, ty = _unit(bx - ax, by - ay)
        return ax + (bx - ax) * t - ty * left, ay + (by - ay) * t + tx * left, tx, ty

    def project(self, px: float, py: float) -> tuple[float, float, float, float]:
        """How far a point is from the centreline, how far along it is, and the way the road runs there."""
        best, along, tx, ty = math.inf, 0.0, self.sx, self.sy
        xs, ys = self.xs, self.ys
        for i in range(1, len(xs)):
            ax, ay = xs[i - 1], ys[i - 1]
            dx, dy = xs[i] - ax, ys[i] - ay
            length_sq = dx * dx + dy * dy
            share = min(1.0, max(0.0, ((px - ax) * dx + (py - ay) * dy) / length_sq)) if length_sq > 0 else 0.0
            distance = math.hypot(px - ax - dx * share, py - ay - dy * share)
            if distance >= best:
                continue
            best = distance
            along = self.along[i - 1] + (self.along[i] - self.along[i - 1]) * share
            tx, ty = _unit(dx, dy)
        return best, along, tx, ty


class RoadNetwork:
    """An area's directed roads, junctions, lights and vehicle sample. Built once, never changed."""

    def __init__(self, area: str, prepared: dict, signal_junctions: tuple[dict, ...]):
        self.area = area
        self.models: dict[str, dict] = prepared["models"]
        self.seeds: list[dict] = prepared["vehicles"]
        self.roads: list[Road] = []
        self.by_key: dict[str, Road] = {}
        self.exits: dict[int, list[Road]] = {}
        for source in prepared["roads"]:
            points = [tuple(p) for p in source.get("points_m") or []]
            if len(points) < 2:
                continue
            self._add(source, 1, points)
            if not source.get("oneway"):
                self._add(source, -1, points[::-1])
        self.has_signals = bool(signal_junctions)
        for junction in signal_junctions:
            for approach in junction.get("approaches", []):
                heading = math.radians(approach["heading_deg"])
                travel = (math.cos(heading), math.sin(heading))
                for direction in (1, -1):
                    road = self.by_key.get(f"{approach['road']}|{direction}")
                    if road is None:
                        continue
                    _, along, tx, ty = road.project(approach["stop_x_m"], approach["stop_y_m"])
                    # The road's other way runs past the same place, and has no light here
                    if tx * travel[0] + ty * travel[1] < STOP_ALIGN:
                        continue
                    road.stops.append((along, int(junction["offset_s"]), int(approach["phase"]), str(junction["id"])))
        for road in self.roads:
            if road.stops:
                road.rank += SIGNAL_RANK
        # Where traffic comes in from outside the area: roads that no other road leads into
        arriving: dict[int, list[Road]] = {}
        for road in self.roads:
            arriving.setdefault(road.to_node, []).append(road)
        self.entries = [road for road in self.roads
                        if not any(other.road_id != road.road_id for other in arriving.get(road.from_node, ()))]
        self.large_entries = [road for road in self.entries if not road.service]
        self.grid: dict[tuple[int, int], list[Road]] = {}
        for road in self.roads:
            for i in range(1, len(road.xs)):
                ax, ay, bx, by = road.xs[i - 1], road.ys[i - 1], road.xs[i], road.ys[i]
                min_x, min_y = self.cell(min(ax, bx) - MATCH_M, min(ay, by) - MATCH_M)
                max_x, max_y = self.cell(max(ax, bx) + MATCH_M, max(ay, by) + MATCH_M)
                for x in range(min_x, max_x + 1):
                    for y in range(min_y, max_y + 1):
                        near = self.grid.setdefault((x, y), [])
                        if not near or near[-1] is not road:
                            if road not in near:
                                near.append(road)
        self._movements: dict[tuple[int, int, int, int], tuple] = {}
        self._conflicts: dict[tuple, bool] = {}
        self._turns: dict[tuple[int, int], tuple[float, float]] = {}

    def _add(self, source: dict, direction: int, points: list[tuple[float, float]]) -> None:
        road = Road(len(self.roads), source, direction, points)
        if road.length < 0.1:
            return
        self.roads.append(road)
        self.by_key[road.key] = road
        self.exits.setdefault(road.from_node, []).append(road)

    def __deepcopy__(self, memo):
        # Shared by every session and never changed: a staged actor keeps the same network
        return self

    @staticmethod
    def cell(x: float, y: float) -> tuple[int, int]:
        return math.floor(x / CELL_M), math.floor(y / CELL_M)

    def near(self, x: float, y: float) -> list[Road]:
        """Every road within reach of a point, and some further."""
        return self.grid.get(self.cell(x, y), [])

    def road_under(self, x: float, y: float, hx: float, hy: float, align: float) -> tuple[Road | None, float]:
        """The road a vehicle at a point heading this way is on, and how far to the left of the point its
        kerb lane's middle is, or (None, 0) off the roads."""
        best, best_along, best_distance = None, 0.0, MATCH_M
        for road in self.near(x, y):
            distance, along, tx, ty = road.project(x, y)
            if distance > best_distance or tx * hx + ty * hy < align:
                continue
            best, best_along, best_distance = road, along, distance
        if best is None:
            return None, 0.0
        lx, ly, _, _ = best.sample(best_along, best.lane_offset(best.lanes - 1))
        return best, (lx - x) * -hy + (ly - y) * hx

    def turn(self, road: Road, nxt: Road) -> tuple[float, float]:
        """For a turn from one road onto the next: how straight on it is (1 straight, 0 back the way it
        came), and how much of a road's speed it is taken at (0 for a hairpin, 1 for straight on)."""
        key = (road.index, nxt.index)
        known = self._turns.get(key)
        if known is None:
            degrees = angle_deg(road.ex, road.ey, nxt.sx, nxt.sy)
            known = self._turns[key] = (inverse_lerp(180.0, 0.0, degrees), inverse_lerp(120.0, 15.0, degrees))
        return known

    def movement(self, road: Road, lane: int, nxt: Road, next_lane: int) -> tuple:
        """The way a vehicle drives through a junction: along its lane to it, across and out along its next lane."""
        key = (road.index, lane, nxt.index, next_lane)
        known = self._movements.get(key)
        if known is None:
            offset, next_offset = road.lane_offset(lane), nxt.lane_offset(next_lane)
            known = self._movements[key] = tuple(
                (x, y) for x, y, _, _ in (road.sample(road.length - MOVEMENT_REACH_M, offset), road.sample(road.length, offset),
                                          nxt.sample(0.0, next_offset), nxt.sample(MOVEMENT_REACH_M, next_offset)))
        return known

    def conflict(self, a: tuple[int, int, int, int], b: tuple[int, int, int, int]) -> bool:
        """Whether two movements through a junction, as (road, lane, next road, next lane), cross."""
        # In the same lane one simply follows the other, whichever way each then turns
        if a[0] == b[0] and a[1] == b[1]:
            return False
        if a[2] == b[2] and a[3] == b[3]:
            return True
        key = (a, b) if a <= b else (b, a)
        known = self._conflicts.get(key)
        if known is None:
            first = self.movement(self.roads[a[0]], a[1], self.roads[a[2]], a[3])
            second = self.movement(self.roads[b[0]], b[1], self.roads[b[2]], b[3])
            known = self._conflicts[key] = any(
                segment_distance(first[i], first[i + 1], second[j], second[j + 1]) < CONFLICT_M
                for i in range(3) for j in range(3))
        return known


@lru_cache(maxsize=4)
def road_network(area: str) -> RoadNetwork | None:
    """An area's network, or None when it has no prepared traffic file."""
    prepared = prepared_area(area)
    if prepared is None:
        return None
    return RoadNetwork(area, prepared, route_signals.junctions(area))
