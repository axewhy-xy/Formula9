"""The walk network the backend's pedestrians walk, read from an area's walkways file.

src.traffic.import_walkways works out the network once, offline, and writes it with the sidewalks the 3D
view draws (fixtures/traffic/<area>-walkways.json); this only reads it, so a walker this far along a
walkway here is at the same spot in the view. Each walkway of the file becomes two directed walkways,
one each way, as a two-way road becomes two directed roads (src.traffic.road_network).

A crossing walkway runs from the walking line of the sidewalk on one side, down to the kerb, over the
road and up from the far kerb. The part between the kerbs is the road part: where a walker is on the
asphalt, and the part the traffic must keep out of while a walker is on it. Each crossing knows the
directed roads whose centrelines it cuts and how far along each it does (by_road), so a vehicle finds
the crossings ahead of it as it finds the stop lines of the lights (Road.stops); and a shuttle route
finds the crossings it passes over (route_crossings).

A signalled crossing carries the junction and phase whose red lets people over. One whose junction is
not among the area's signals is left out, never left free to cross on any light, as the views do.

Each crossing also knows, for each road it cuts, where that road's centreline meets it along its line and
how a metre to the left of that road maps onto the line (Crossing.lanes_at): so a vehicle in one lane can
be let go once every walker on the crossing has passed that lane (per-lane release, plan phase 5).

Where walkers go (plan phase 5): the attractors of the file (homes, places, bus stops and interchanges),
each at a walk node. A walker on a trip follows the shortest way there (next_hop), a crossing counting as a
detour of the time it usually costs to wait at it; how many walkers are out follows the hour of the day
(density), the rest being indoors or away on a bus. A file without attractors leaves the walkers walking at
random, as before.

Nothing here changes once built, so every session shares one network per area (the routing trees it works
out as walkers need them are the same whoever asks first). Pedestrians are walked by both traffic engines
(src.traffic.city_traffic, src.traffic.native_traffic) unless F9_PEDESTRIANS is 0.

Points are planar metres, x east and y north, as in the prepared files.
"""

from __future__ import annotations

import heapq
import json
import math
import os
from array import array
from functools import lru_cache
from pathlib import Path

from src.traffic import route_signals
from src.traffic.import_vehicle_population import OUTPUT_DIRECTORY
from src.traffic.road_network import RoadNetwork, road_network

PEDESTRIANS_VARIABLE = "F9_PEDESTRIANS"
# When a session's traffic clock starts, as a time of day: HH:MM (08:00 unless set)
DAY_START_VARIABLE = "F9_DAY_START"
DEFAULT_DAY_START_S = 8 * 3600
DAY_S = 86400

SIDEWALK, LINK, CROSSING = 0, 1, 2
# A footway (a footpath mapped as a way of its own) is walked as a sidewalk is
_KINDS = {"sidewalk": SIDEWALK, "link": LINK, "crossing": CROSSING, "footway": SIDEWALK}
# What an attractor is: a home or a place is gone into; at a stop or an interchange a walker waits, then
# leaves on a bus
HOME, PLACE, STOP, INTERCHANGE = 0, 1, 2, 3
_ATTRACTOR_KINDS = {"home": HOME, "place": PLACE, "bus_stop": STOP, "interchange": INTERCHANGE}
# A trip's way counts a crossing as this many metres more than its length: about the wait it usually costs,
# at a walking pace (20 s at a light, 5 s elsewhere, at 1.3 m/s)
SIGNAL_DETOUR_M, CROSSING_DETOUR_M = 26.0, 6.5
# How a crossing is controlled: by a light whose red lets people over, a zebra (people have priority), or
# nothing (a side road's mouth: people wait for a gap)
SIGNAL, ZEBRA, UNCONTROLLED = 0, 1, 2
_CONTROLS = {"signal": SIGNAL, "zebra": ZEBRA, "uncontrolled": UNCONTROLLED}

# Half a walker's body, a 0.5 m square seen from above
WALKER_HALF_M = 0.25
# A crossing's band along a road it cuts at a slant is wider; this much slant at most is allowed for
MIN_CROSSING_SINE = 0.5
# A crossing line is taken this much further past its kerbs when finding the roads it cuts
CUT_REACH_M = 0.5
# The spatial index of the crossings, for shuttle routes
CELL_M = 40.0


def enabled() -> bool:
    """Whether sessions' background traffic walks pedestrians (F9_PEDESTRIANS, on unless 0)."""
    value = os.environ.get(PEDESTRIANS_VARIABLE, "1").strip().lower()
    if value in ("0", "off", "false", "no"):
        return False
    if value in ("", "1", "on", "true", "yes"):
        return True
    raise ValueError(f"{PEDESTRIANS_VARIABLE} must be 0 or 1, not {value!r}")


def day_start_s() -> float:
    """The time of day a new session's traffic clock starts at, in seconds after midnight (F9_DAY_START,
    HH:MM, 08:00 unless set)."""
    value = os.environ.get(DAY_START_VARIABLE, "").strip()
    if not value:
        return float(DEFAULT_DAY_START_S)
    hours, _, minutes = value.partition(":")
    try:
        hour, minute = int(hours), int(minutes or 0)
    except ValueError:
        raise ValueError(f"{DAY_START_VARIABLE} must be a time of day as HH:MM, not {value!r}") from None
    if not (0 <= hour < 24 and 0 <= minute < 60):
        raise ValueError(f"{DAY_START_VARIABLE} must be a time of day as HH:MM, not {value!r}")
    return float(hour * 3600 + minute * 60)


def session_day_start_s(pedestrians: bool, given: float | None = None) -> float:
    """The time of day a session's traffic clock starts at: the one given (a checkpoint's), else F9_DAY_START's
    with pedestrians. Without them nothing depends on it, so F9_DAY_START is not read, and a malformed value
    cannot stop traffic that walks nobody."""
    if given is not None:
        return float(given)
    return day_start_s() if pedestrians else float(DEFAULT_DAY_START_S)


def walkways_path(area: str) -> Path:
    return OUTPUT_DIRECTORY / f"{area}-walkways.json"


def _unit(x: float, y: float) -> tuple[float, float]:
    length = math.hypot(x, y)
    return (x / length, y / length) if length > 1e-9 else (0.0, 0.0)


def _cut(ax, ay, bx, by, cx, cy, dx, dy) -> tuple[float, float] | None:
    """Where segment a-b meets segment c-d, as shares along each, or None where they do not."""
    rx, ry, sx, sy = bx - ax, by - ay, dx - cx, dy - cy
    denominator = rx * sy - ry * sx
    if abs(denominator) < 1e-12:
        return None
    t = ((cx - ax) * sy - (cy - ay) * sx) / denominator
    u = ((cx - ax) * ry - (cy - ay) * rx) / denominator
    if 0.0 <= t <= 1.0 and 0.0 <= u <= 1.0:
        return t, u
    return None


class Crossing:
    """A crossing as the traffic sees it: its road part as a rectangle (middle, direction, half length and
    half width), how it is controlled, and the directed roads it cuts as (road index, metres along, how far
    either side of that its band reaches along the road, and its slant: how much sooner a body's front
    corner reaches the band than its middle, a metre of the body's half width).

    lanes_at maps each road it cuts to (where that road's centreline meets its line, in metres from its
    first kerb a; and the sine of the angle between them, signed: a metre to the left of the road is
    1/sine metres along the crossing)."""

    __slots__ = ("index", "crossing_id", "control", "junction", "offset", "phase", "cx", "cy", "dx", "dy",
                 "half_length", "half_width", "ax", "ay", "bx", "by", "roads", "lanes_at")

    def __deepcopy__(self, memo):
        return self


class Attractor:
    """Somewhere walkers go: its kind (HOME, PLACE, STOP or INTERCHANGE), the walk node it is at, how much
    it draws walkers, and where that node is."""

    __slots__ = ("index", "attractor_id", "kind", "node", "weight", "x", "y")

    def __deepcopy__(self, memo):
        return self


class Walkway:
    """One way along one walkway of the file: its line, width and sub-lanes, the nodes it joins, the
    walkway back the other way, and for a crossing where its road part starts and ends along it and which
    way along the crossing's line (from kerb a to b: 1, back: -1) it runs. A trip counts it as cost metres."""

    __slots__ = ("index", "source", "kind", "crossing", "xs", "ys", "along", "length", "width", "lanes", "mine",
                 "from_node", "to_node", "back", "kerb_in", "kerb_out", "sx", "sy", "ex", "ey", "axis", "cost")

    def __deepcopy__(self, memo):
        return self

    def sample(self, distance: float, left: float) -> tuple[float, float, float, float]:
        """The point this far along and this far to the left, and the way the walkway runs there."""
        along = self.along
        d = min(max(distance, 0.0), self.length)
        # Walkways are short: a straight search beats bisect's call
        index = 1
        last = len(along) - 1
        while index < last and along[index] < d:
            index += 1
        ax, ay, bx, by = self.xs[index - 1], self.ys[index - 1], self.xs[index], self.ys[index]
        segment = along[index] - along[index - 1]
        t = (d - along[index - 1]) / segment if segment > 1e-9 else 0.0
        tx, ty = _unit(bx - ax, by - ay)
        if tx == 0.0 and ty == 0.0:
            tx, ty = self.sx, self.sy
        return ax + (bx - ax) * t - ty * left, ay + (by - ay) * t + tx * left, tx, ty


class WalkNetwork:
    """An area's directed walkways, the nodes they join, its crossings and where walkers start. Built
    once against the area's road network, never changed."""

    def __init__(self, area: str, data: dict, network: RoadNetwork, signal_junctions: tuple[dict, ...] | list[dict]):
        self.area = area
        self.network = network
        self.source_hash = str(data.get("source_hash") or "")
        offsets = {str(j["id"]): int(j["offset_s"]) for j in signal_junctions}
        self.nodes: list[tuple[float, float]] = [tuple(node["xy_m"]) for node in data["nodes"]]
        self.ways: list[Walkway] = []
        self.crossings: list[Crossing] = []
        self.exits: list[list[Walkway]] = [[] for _ in self.nodes]
        # Signalled crossings left out for want of their junction's light
        self.without_light = 0
        forward: dict[int, Walkway] = {}
        for source in data["walkways"]:
            kind = _KINDS.get(source.get("kind"), SIDEWALK)
            points = [tuple(point) for point in source.get("points_m") or ()]
            if len(points) < 2:
                continue
            crossing = None
            if kind == CROSSING:
                control = _CONTROLS.get(source.get("control"), UNCONTROLLED)
                junction = str(source.get("junction_id") or "")
                if control == SIGNAL and junction not in offsets:
                    self.without_light += 1
                    continue
                crossing = self._crossing(source, points, control, junction, offsets.get(junction, 0))
            ahead = self._add(source, kind, crossing, points, source["from"], source["to"])
            back = self._add(source, kind, crossing, points[::-1], source["to"], source["from"])
            ahead.back, back.back = back.index, ahead.index
            forward[int(source["id"])] = ahead
            if crossing is not None:
                self._kerbs(ahead, back, points)
        for crossing in self.crossings:
            self._cut_roads(crossing)
        # Road index -> (metres along, crossing, half band, slant) of each crossing it passes, in order along it
        self.by_road: list[list[tuple[float, Crossing, float, float]]] = [[] for _ in network.roads]
        for crossing in self.crossings:
            for road_index, along, half, slant in crossing.roads:
                self.by_road[road_index].append((along, crossing, half, slant))
        for entries in self.by_road:
            if len(entries) > 1:
                entries.sort(key=lambda entry: (entry[0], entry[1].index))
        # Node -> the directed roads ending there, to look back along the way traffic comes to a crossing
        self.arriving: dict[object, list] = {}
        for road in network.roads:
            self.arriving.setdefault(road.to_node, []).append(road)
        # Where walkers start: (seed index, id, walkway, metres along, desired speed, height, model)
        self.seeds: list[tuple[int, str, Walkway, float, float, float, int]] = []
        for index, seed in enumerate(data.get("seeds") or ()):
            way = forward.get(int(seed.get("walkway", -1)))
            if way is None:
                continue
            self.seeds.append((index, str(seed["id"]), way, float(seed.get("distance_m") or 0.0),
                               float(seed.get("speed_mps") or 1.3), float(seed.get("height_m") or 1.7),
                               int(seed.get("model") or 0)))
        self.grid: dict[tuple[int, int], list[Crossing]] = {}
        for crossing in self.crossings:
            low_x, low_y = self.cell(min(crossing.ax, crossing.bx), min(crossing.ay, crossing.by))
            high_x, high_y = self.cell(max(crossing.ax, crossing.bx), max(crossing.ay, crossing.by))
            for x in range(low_x, high_x + 1):
                for y in range(low_y, high_y + 1):
                    self.grid.setdefault((x, y), []).append(crossing)
        self._routes: dict[str, list[tuple[float, Crossing, float]]] = {}
        # Node -> the walkways ending there, in order: a trip's way is worked out back from where it goes
        self.entering: list[list[Walkway]] = [[] for _ in self.nodes]
        for way in self.ways:
            self.entering[way.to_node].append(way)
        # Where walkers go, at nodes some walkway leaves; none in a file from before trips
        self.attractors: list[Attractor] = []
        for source in data.get("attractors") or ():
            node = int(source["node"])
            kind = _ATTRACTOR_KINDS.get(source.get("kind"))
            if kind is None or not 0 <= node < len(self.nodes) or not self.exits[node]:
                continue
            attractor = Attractor.__new__(Attractor)
            attractor.index = len(self.attractors)
            attractor.attractor_id = str(source["id"])
            attractor.kind = kind
            attractor.node = node
            attractor.weight = float(source.get("weight") or 1.0)
            attractor.x, attractor.y = self.nodes[node]
            self.attractors.append(attractor)
        # The homes walkers start in (any attractor, with none), and the stops a bus brings them back to
        self.homes: list[Attractor] = [a for a in self.attractors if a.kind == HOME] or self.attractors
        self.stops: list[Attractor] = [a for a in self.attractors if a.kind in (STOP, INTERCHANGE)]
        # The share of the walkers out by the hour from midnight; all of them, without a curve
        hourly = (data.get("population") or {}).get("hourly") or ()
        self.hourly: tuple[float, ...] = tuple(min(max(float(v), 0.0), 1.0) for v in hourly) if len(hourly) == 24 else ()
        # Target node -> the walkway each node's walkers take towards it (-1 where none leads there)
        self._trees: dict[int, array] = {}
        # The walkways (not crossings) by where they run, for placing a walker back on the network
        self._way_grid: dict[tuple[int, int], list[Walkway]] | None = None

    def __deepcopy__(self, memo):
        # Shared by every session and never changed, as the road network is
        return self

    @staticmethod
    def cell(x: float, y: float) -> tuple[int, int]:
        return math.floor(x / CELL_M), math.floor(y / CELL_M)

    def _add(self, source: dict, kind: int, crossing: Crossing | None, points, start: int, end: int) -> Walkway:
        way = Walkway.__new__(Walkway)
        way.index = len(self.ways)
        way.source = int(source["id"])
        way.kind = kind
        way.crossing = crossing
        way.xs = [float(p[0]) for p in points]
        way.ys = [float(p[1]) for p in points]
        along = [0.0]
        for (ax, ay), (bx, by) in zip(points, points[1:]):
            along.append(along[-1] + math.hypot(bx - ax, by - ay))
        way.along = along
        way.length = max(along[-1], 0.01)
        way.width = float(source.get("width_m") or 1.5)
        way.lanes = max(1, int(source.get("lanes") or 1))
        # The sub-lanes on a walker's own (left) side, Singapore keeping left
        way.mine = max(1, way.lanes // 2)
        way.from_node, way.to_node = int(start), int(end)
        way.back = -1
        way.kerb_in = way.kerb_out = 0.0
        way.axis = 0
        way.sx, way.sy = _unit(points[1][0] - points[0][0], points[1][1] - points[0][1])
        way.ex, way.ey = _unit(points[-1][0] - points[-2][0], points[-1][1] - points[-2][1])
        detour = 0.0 if crossing is None else SIGNAL_DETOUR_M if crossing.control == SIGNAL else CROSSING_DETOUR_M
        way.cost = way.length + detour
        self.ways.append(way)
        self.exits[way.from_node].append(way)
        return way

    def _crossing(self, source: dict, points, control: int, junction: str, offset: int) -> Crossing:
        crossing = Crossing.__new__(Crossing)
        crossing.index = len(self.crossings)
        crossing.crossing_id = str(source.get("crossing_id") or source["id"])
        crossing.control = control
        crossing.junction = junction
        crossing.offset = offset
        crossing.phase = int(source.get("phase") or 0)
        (crossing.ax, crossing.ay), (crossing.bx, crossing.by) = _road_part(points)
        crossing.cx, crossing.cy = (crossing.ax + crossing.bx) / 2, (crossing.ay + crossing.by) / 2
        crossing.dx, crossing.dy = _unit(crossing.bx - crossing.ax, crossing.by - crossing.ay)
        crossing.half_length = math.hypot(crossing.bx - crossing.ax, crossing.by - crossing.ay) / 2
        crossing.half_width = float(source.get("width_m") or 3.0) / 2
        crossing.roads = []
        crossing.lanes_at = {}
        self.crossings.append(crossing)
        return crossing

    @staticmethod
    def _kerbs(ahead: Walkway, back: Walkway, points) -> None:
        """Where the road part starts and ends along each way of a crossing."""
        (ax, ay), (bx, by) = _road_part(points)
        best_a = best_b = 0.0
        found_a = found_b = math.inf
        for i, (px, py) in enumerate(points):
            if math.hypot(px - ax, py - ay) < found_a:
                found_a, best_a = math.hypot(px - ax, py - ay), ahead.along[i]
            if math.hypot(px - bx, py - by) < found_b:
                found_b, best_b = math.hypot(px - bx, py - by), ahead.along[i]
        ahead.kerb_in, ahead.kerb_out = min(best_a, best_b), max(best_a, best_b)
        back.kerb_in, back.kerb_out = ahead.length - ahead.kerb_out, ahead.length - ahead.kerb_in
        # The way that reaches kerb a first runs from a to b along the crossing's line
        ahead.axis = 1 if best_a <= best_b else -1
        back.axis = -ahead.axis

    def _cut_roads(self, crossing: Crossing) -> None:
        """The directed roads whose centrelines the crossing's road part (a little longer) cuts."""
        crossing.roads, crossing.lanes_at = cut_roads(self.network, crossing.ax, crossing.ay, crossing.bx, crossing.by,
                                                      crossing.dx, crossing.dy, crossing.half_width)

    # ---------------------------------------------------------------- trips

    def next_hop(self, target: int) -> array:
        """For every node, the walkway a walker there takes on the shortest way to the target node (each
        crossing a detour of the wait it usually costs), or -1 where none leads there or it is there.
        Worked out once per target, the first time a walker needs it."""
        tree = self._trees.get(target)
        if tree is not None:
            return tree
        count = len(self.nodes)
        cost = [math.inf] * count
        hop = array("i", [-1]) * count
        cost[target] = 0.0
        heap = [(0.0, target)]
        entering = self.entering
        while heap:
            reached, node = heapq.heappop(heap)
            if reached > cost[node]:
                continue
            for way in entering[node]:
                start = way.from_node
                through = reached + way.cost
                if through < cost[start]:
                    cost[start] = through
                    hop[start] = way.index
                    heapq.heappush(heap, (through, start))
        self._trees[target] = hop
        return hop

    def density(self, time_of_day_s: float) -> float:
        """The share of the walkers out at this time of day (seconds after midnight): the hourly curve's,
        from each hour's value to the next's; all of them without a curve."""
        hourly = self.hourly
        if not hourly:
            return 1.0
        hour = (time_of_day_s % DAY_S) / 3600.0
        index = math.floor(hour)
        share = hour - index
        index = min(max(index, 0), 23)
        here, after = hourly[index], hourly[(index + 1) % 24]
        return here + (after - here) * share

    # ---------------------------------------------------------------- putting a walker back

    def nearest_way(self, x: float, y: float, reach: float) -> tuple[Walkway, float, float] | None:
        """The walkway (not a crossing) passing nearest a point within reach, how far along it that is, and
        how far off; the lowest numbered of equals. None with none in reach."""
        grid = self._way_grid
        if grid is None:
            grid = {}
            for way in self.ways:
                if way.crossing is not None:
                    continue
                low_x, low_y = self.cell(min(way.xs), min(way.ys))
                high_x, high_y = self.cell(max(way.xs), max(way.ys))
                for cx in range(low_x, high_x + 1):
                    for cy in range(low_y, high_y + 1):
                        grid.setdefault((cx, cy), []).append(way)
            self._way_grid = grid
        low_x, low_y = self.cell(x - reach, y - reach)
        high_x, high_y = self.cell(x + reach, y + reach)
        best = None
        for cx in range(low_x, high_x + 1):
            for cy in range(low_y, high_y + 1):
                for way in grid.get((cx, cy), ()):
                    for i in range(1, len(way.xs)):
                        ax, ay, bx, by = way.xs[i - 1], way.ys[i - 1], way.xs[i], way.ys[i]
                        dx, dy = bx - ax, by - ay
                        length2 = dx * dx + dy * dy
                        t = 0.0 if length2 <= 1e-12 else min(max(((x - ax) * dx + (y - ay) * dy) / length2, 0.0), 1.0)
                        off = math.hypot(ax + dx * t - x, ay + dy * t - y)
                        if off <= reach and (best is None or (off, way.index) < (best[2], best[0].index)):
                            best = (way, way.along[i - 1] + (way.along[i] - way.along[i - 1]) * t, off)
        return best

    def route_crossings(self, route) -> list[tuple[float, Crossing, float]]:
        """The crossings a shuttle route passes over, as (metres along the route, crossing, half band), in
        order along it. Worked out once per route's geometry."""
        known = self._routes.get(route.sig)
        if known is not None:
            return known
        found: list[tuple[float, Crossing, float]] = []
        taken: set[int] = set()
        points, along = route.points, route.along
        for i in range(1, len(points)):
            (px, py), (qx, qy) = points[i - 1], points[i]
            low_x, low_y = self.cell(min(px, qx), min(py, qy))
            high_x, high_y = self.cell(max(px, qx), max(py, qy))
            near: list[Crossing] = []
            for x in range(low_x, high_x + 1):
                for y in range(low_y, high_y + 1):
                    for crossing in self.grid.get((x, y), ()):
                        if crossing not in near:
                            near.append(crossing)
            near.sort(key=lambda crossing: crossing.index)
            for crossing in near:
                ax, ay = crossing.ax - crossing.dx * CUT_REACH_M, crossing.ay - crossing.dy * CUT_REACH_M
                bx, by = crossing.bx + crossing.dx * CUT_REACH_M, crossing.by + crossing.dy * CUT_REACH_M
                hit = _cut(px, py, qx, qy, ax, ay, bx, by)
                if hit is None:
                    continue
                at = along[i - 1] + (along[i] - along[i - 1]) * hit[0]
                # A loop passes the same crossing again a lap later; a route doubling back passes it again
                if (crossing.index, round(at)) in taken:
                    continue
                taken.add((crossing.index, round(at)))
                tx, ty = _unit(qx - px, qy - py)
                sine = abs(tx * crossing.dy - ty * crossing.dx)
                found.append((at, crossing, crossing.half_width / max(sine, MIN_CROSSING_SINE) + WALKER_HALF_M))
        found.sort(key=lambda entry: (entry[0], entry[1].index))
        self._routes[route.sig] = found
        return found


def cut_roads(network: RoadNetwork, ax0: float, ay0: float, bx0: float, by0: float, dx: float, dy: float,
              half_width: float) -> tuple[list[tuple[int, float, float, float]], dict[int, tuple[float, float]]]:
    """The directed roads whose centrelines a line from kerb a to kerb b (running dx, dy), carried on a
    little past each, cuts: each as (road index, metres along it, the band either side, slant), for a band
    half_width either side of the line; and for each, where its centreline meets the line (metres from a)
    and the signed sine of their angle (Crossing.lanes_at)."""
    ax, ay = ax0 - dx * CUT_REACH_M, ay0 - dy * CUT_REACH_M
    bx, by = bx0 + dx * CUT_REACH_M, by0 + dy * CUT_REACH_M
    span = math.hypot(bx - ax, by - ay)
    low_x, low_y = network.cell(min(ax, bx), min(ay, by))
    high_x, high_y = network.cell(max(ax, bx), max(ay, by))
    seen: set[int] = set()
    candidates = []
    for x in range(low_x, high_x + 1):
        for y in range(low_y, high_y + 1):
            for road in network.grid.get((x, y), ()):
                if road.index not in seen:
                    seen.add(road.index)
                    candidates.append(road)
    candidates.sort(key=lambda road: road.index)
    roads: list[tuple[int, float, float, float]] = []
    lanes_at: dict[int, tuple[float, float]] = {}
    for road in candidates:
        xs, ys = road.xs, road.ys
        for i in range(1, len(xs)):
            hit = _cut(xs[i - 1], ys[i - 1], xs[i], ys[i], ax, ay, bx, by)
            if hit is None:
                continue
            tx, ty = _unit(xs[i] - xs[i - 1], ys[i] - ys[i - 1])
            sine = abs(tx * dy - ty * dx)
            half = half_width / max(sine, MIN_CROSSING_SINE) + WALKER_HALF_M
            along = road.along[i - 1] + (road.along[i] - road.along[i - 1]) * hit[0]
            roads.append((road.index, along, half, _slant(sine)))
            lanes_at[road.index] = (hit[1] * span - CUT_REACH_M, tx * dy - ty * dx)
            break
    return roads, lanes_at


def _slant(sine: float) -> float:
    """How much further along a road a body's front corner meets a crossing than its middle does, a metre
    of its half width, where the road meets the crossing at this sine of an angle."""
    return math.sqrt(max(0.0, 1.0 - sine * sine)) / max(sine, MIN_CROSSING_SINE)


def _road_part(points) -> tuple[tuple[float, float], tuple[float, float]]:
    """The kerbs of a crossing's line: its second and third points, as the importer writes it (walking
    line, kerb, kerb, walking line); where it merged a kerb into a walking line, its longest piece."""
    if len(points) == 4:
        return tuple(points[1]), tuple(points[2])
    best, pair = -1.0, (tuple(points[0]), tuple(points[-1]))
    for a, b in zip(points, points[1:]):
        length = math.dist(a, b)
        if length > best:
            best, pair = length, (tuple(a), tuple(b))
    return pair


def from_file(area: str, network: RoadNetwork, path: Path | None = None,
              signal_junctions: tuple[dict, ...] | None = None) -> WalkNetwork | None:
    """An area's walk network from its walkways file, or None when it has none."""
    file = path or walkways_path(area)
    if not file.is_file():
        return None
    data = json.loads(file.read_text(encoding="utf-8"))
    junctions = route_signals.junctions(area) if signal_junctions is None else signal_junctions
    return WalkNetwork(area, data, network, junctions)


@lru_cache(maxsize=4)
def walk_network(area: str) -> WalkNetwork | None:
    """An area's walk network on its road network, or None without a prepared file or walkways file."""
    network = road_network(area)
    if network is None:
        return None
    return from_file(area, network)
