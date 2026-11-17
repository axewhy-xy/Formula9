"""Background traffic the backend drives, and the shuttles driving through it.

The vehicles are the area's prepared sample of Singapore's registered vehicles, on the roads of
src.traffic.road_network, under the made-up light timings of src.traffic.signal_cycle. How they drive is
ported from the Unity client's CityTrafficRenderer (CityTrafficDriving.cs), which only draws what this
drives once a session's traffic comes from here. Each step, in order: vehicles a hazard has caught
leave the area, every vehicle looks along the roads it plans to take and asks for the next junction,
the junctions answer, and every vehicle picks a speed from the same picture of the road (the Krauss
car-following model), so none reacts to a move another made in the same step.

The shuttles are the session's own. The session actor moves them; this tells it how fast and how far a
shuttle may go for the vehicles in its way (plan_shuttle), and records where it went (place_shuttle):
in the kerb lane of the road its route runs along, its body as the bus model the 3D view draws. The
vehicles keep clear of a shuttle, follow it, queue behind it at a light and give way to it where it gives
way to them, as they did in the Unity client.

World events play out in it (src.services.incident_scenarios.world_events): a breakdown or a collision
pins vehicles dead in their lane (pin_vehicle), and a signal fault sticks a junction's lights on red
(set_signal_fault). Nothing scripts the jam either causes: the traffic behind queues of its own accord,
and a shuttle meets the queue as any vehicle does. The usual jam release still applies, except to the
pinned vehicles themselves, which stay until their event clears.

Everything is deterministic: the same session from the same state drives the same way, and state() and
from_state() carry it through a checkpoint exactly. Unity's float arithmetic is not reproduced, so the
two never ran in lockstep; the client now draws this traffic instead of driving its own.

This is the source of truth for how the traffic and the shuttles in it drive. The client's
CityTrafficDriving.cs and CityTrafficShuttles.cs remain only as a fallback, for a session whose backend
sends no traffic frames: change the driving here, and port a change there only if the fallback should
match.
"""

from __future__ import annotations

import bisect
import math
import struct
from copy import deepcopy

from src.traffic import signal_cycle
from src.traffic.road_network import LANE_WIDTH_M, Road, RoadNetwork, angle_deg, class_weight, inverse_lerp, lerp, \
    road_network

# How far ahead a vehicle has chosen its roads
PLAN_AHEAD_M = 80.0
MAX_PLANNED_ROADS = 8
# The Krauss model: a speed from which the vehicle can always stop in time, whatever the vehicle in front
# does, so long as a step is no longer than the reaction time
REACTION_S, COMFORT_BRAKE, HARD_BRAKE = 1.0, 3.5, 9.0
# A driver stops for a yellow if this firm a stop will do; anyone closer has time to clear the line
YELLOW_BRAKE = 6.0
MIN_GAP_M, STOP_MARGIN_M, NODE_STOP_BACK_M = 2.0, 0.5, 3.0
# A front this far past a stop line has crossed it, and the light no longer applies
PAST_LINE_M = 1.5
# Asks for a junction this far beyond its braking distance, and is seen by others from further off
CLAIM_MARGIN_M, NOTICE_S = 8.0, 5.0
# A rear this far past the junction has left it
CLEARED_JUNCTION_M = 2.0
# Waiting this long counts for one road class, so a side road gets out in the end
WAIT_PER_RANK_S = 5.0
# A leader slower than this is standing, so the road has no room behind it
STANDING_MPS, STOPPED_MPS = 2.0, 0.3
# Stopped this long, a vehicle gives back a junction it has not entered, for one blocked behind it to go
RELEASE_CLAIM_S = 3.0
# Past any red light's wait: stopped this long is a jam nothing will clear, so the vehicle leaves the area
STUCK_S = 90.0
# It comes in from off the map no faster than this, and from rest where a light is close by
ENTER_MPS = 5.0
# A hazard's line meets a road this close, running this much its way, along at least this much of it
HAZARD_MATCH_M, HAZARD_STEP_M, HAZARD_MIN_OVERLAP_M, HAZARD_SNAP_M, HAZARD_PARALLEL = 5.0, 4.0, 6.0, 4.0, 0.7
# How vehicles treat a shuttle
SHUTTLE_SIGHT_M, SHUTTLE_CLEARANCE_M, SHUTTLE_BESIDE_M, SHUTTLE_MOVING_MPS = 60.0, 0.3, 0.15, 1.0
SHUTTLE_PASS_BEHIND_M, SHUTTLE_PASS_AHEAD_M = 8.0, 30.0
MAX_ASIDE_M, ASIDE_MPS, PASSING_MPS, SHUTTLE_JUNCTION_M, STANDING_SHUTTLE_JUNCTION_M = 3.5, 1.5, 5.0, 25.0, 8.0
# How a shuttle drives
SHUTTLE_ACCEL, SHUTTLE_LANE_MPS = 1.5, 1.0
SHUTTLE_LOOK_M, SHUTTLE_LOOK_STEP_M = 60.0, 2.0
SHUTTLE_SIDE_ROOM_M, SHUTTLE_ONCOMING_ROOM_M = 0.3, -0.1
SHUTTLE_ROAD_ALIGN = 0.7
SHUTTLE_BEND_SPAN_M, SHUTTLE_BEND_DEG, SHUTTLE_FACING_SPAN_M = 8.0, 15.0, 4.0
# Kept waiting at a junction by traffic this long, a shuttle takes its turn; this long, and what it waits
# for is stuck, so that traffic leaves the area as a jammed vehicle does
SHUTTLE_TAKE_TURN_S, SHUTTLE_GIVE_UP_S = 4.0, 30.0
# A shuttle watches one coming across its way this far ahead, or this long ahead at its speed
SHUTTLE_CROSS_NOTICE_M, SHUTTLE_CROSS_NOTICE_S = 40.0, 4.0
# A shuttle moved further than this along its route in one step was put somewhere new, not driven there
SHUTTLE_JUMP_M = 5.0
# A vehicle whose way runs into a standing shuttle within this far cannot go before it
RUN_INTO_M = 16.0
# A vehicle's middle this close to a shuttle's, either way, is checked against its body before it moves
SHUTTLE_BODY_REACH_M = 20.0
# How much further back than a car a shuttle stays behind another shuttle, and a car behind a shuttle
SHUTTLE_FOLLOW_EXTRA_M, SHUTTLE_SWING_M = 4.0, 2.0
# Bodies closer than this are only touching, as two lanes' vehicles side by side may be
TOUCH_M = 0.1
# What stands in for "no limit" in a checkpoint, which JSON cannot hold as infinity
NO_LIMIT = 1e9
# A world event pins vehicles where it happens (pin_vehicle): one from its lane within this far of the
# place stops there, else one comes from outside the area; a collision's second stands this close behind
# the first
PIN_PICK_M, PIN_TOUCH_M = 30.0, 0.3
# A shuttle squeezes past a pinned vehicle beside its way, not in it, no faster than this
PINNED_PASS_MPS = 2.5
# One vehicle of a frame, little-endian: its index in the area's sample (uint16), x and y in centimetres
# (int32), its heading in tenths of a degree anticlockwise from east (int16) and its speed in cm/s
# (uint16). Packed, a frame of the whole sample is 14 bytes a vehicle, which the 3D view reads without
# parsing thousands of JSON numbers ten times a second.
FRAME_RECORD = struct.Struct("<HiihH")
_INT32 = 2**31 - 1

_PHASE_S, _CYCLE_S, _GREEN_S, _AMBER_S = signal_cycle.PHASE_S, signal_cycle.CYCLE_S, signal_cycle.GREEN_S, \
    signal_cycle.GREEN_S + signal_cycle.YELLOW_S


def krauss(speed: float, gap: float, leader_speed: float, reaction: float = REACTION_S) -> float:
    """The fastest a vehicle can go and still stop behind its leader if the leader brakes."""
    if gap <= 0:
        return 0.0
    return max(0.0, leader_speed + (gap - leader_speed * reaction) / ((speed + leader_speed) / (2 * COMFORT_BRAKE) + reaction))


def light(time_s: float, offset_s: int, phase: int, fault_until_s: float | None = None) -> int:
    """0 green, 1 yellow, 2 red: signal_cycle.phase_state's colour, without building a dict. A junction
    faulted until fault_until_s is red until then."""
    if fault_until_s is not None and time_s < fault_until_s:
        return 2
    into = (time_s + offset_s - phase * _PHASE_S) % _CYCLE_S
    return 0 if into < _GREEN_S else 1 if into < _AMBER_S else 2


def fnv_hash(text: str) -> int:
    """The Unity client's hash of a vehicle's id, which seeds its choices."""
    value = 2166136261
    for character in text or "":
        value = ((value ^ ord(character)) * 16777619) & 0xFFFFFFFF
    return value or 1


def rng_step(state: int) -> int:
    return (1664525 * state + 1013904223) & 0xFFFFFFFF


def random01(state: int) -> float:
    return (rng_step(state) & 0x00FFFFFF) / 16777215.0


def move_towards(current: float, target: float, max_delta: float) -> float:
    if abs(target - current) <= max_delta:
        return target
    return current + math.copysign(max_delta, target - current)


def overlap(ax, ay, afx, afy, a_half_length, a_half_width, bx, by, bfx, bfy, b_half_length, b_half_width) -> bool:
    """Whether two vehicles' bodies, as rectangles seen from above, overlap by more than a touch."""
    for px, py in ((afx, afy), (-afy, afx), (bfx, bfy), (-bfy, bfx)):
        a_reach = a_half_length * abs(afx * px + afy * py) + a_half_width * abs(-afy * px + afx * py)
        b_reach = b_half_length * abs(bfx * px + bfy * py) + b_half_width * abs(-bfy * px + bfx * py)
        if abs((bx - ax) * px + (by - ay) * py) > a_reach + b_reach - TOUCH_M:
            return False
    return True


def line_length(points) -> float:
    return sum(math.dist(a, b) for a, b in zip(points, points[1:]))


def line_at(points, distance: float) -> tuple[float, float, float, float]:
    """The point this far along a line, kept on it, and the way the line runs there."""
    hx, hy = _direction(points[0], points[1]) if len(points) > 1 else (0.0, 1.0)
    left = max(0.0, distance)
    for a, b in zip(points, points[1:]):
        step = math.dist(a, b)
        if step <= 0:
            continue
        hx, hy = (b[0] - a[0]) / step, (b[1] - a[1]) / step
        if left <= step:
            return a[0] + hx * left, a[1] + hy * left, hx, hy
        left -= step
    return points[-1][0], points[-1][1], hx, hy


def _direction(a, b) -> tuple[float, float]:
    length = math.dist(a, b)
    return ((b[0] - a[0]) / length, (b[1] - a[1]) / length) if length > 0 else (0.0, 0.0)


class Claim:
    """A vehicle's right to go through a junction, from when the junction gives way until it has left."""

    __slots__ = ("vehicle", "node", "from_r", "from_lane", "to_r", "to_lane", "entered", "movement")

    def __init__(self, vehicle, node, from_r, from_lane, to_r, to_lane, entered=False):
        self.vehicle, self.node, self.from_r, self.from_lane, self.to_r, self.to_lane, self.entered = \
            vehicle, node, from_r, from_lane, to_r, to_lane, entered
        self.movement = (from_r.index, from_lane, to_r.index, to_lane)


class Vehicle:
    """One vehicle of the sample: where it is, how fast it goes and where it means to go."""

    __slots__ = ("slot", "seed", "id", "model", "length", "half", "half_width", "accel", "large", "speed_factor",
                 "rng", "road", "lane", "lane_index", "distance", "speed", "target_speed", "stopped_s", "wait_s",
                 "aside", "target_aside", "outside", "ahead", "claims", "x", "y", "fx", "fy", "placed")

    def clone(self) -> Vehicle:
        # Written out: a staged session copies every vehicle every step
        c = Vehicle.__new__(Vehicle)
        c.slot, c.seed, c.id, c.model, c.length, c.half, c.half_width = \
            self.slot, self.seed, self.id, self.model, self.length, self.half, self.half_width
        c.accel, c.large, c.speed_factor, c.rng, c.road, c.lane, c.lane_index = \
            self.accel, self.large, self.speed_factor, self.rng, self.road, self.lane, self.lane_index
        c.distance, c.speed, c.target_speed, c.stopped_s, c.wait_s, c.aside, c.target_aside = \
            self.distance, self.speed, self.target_speed, self.stopped_s, self.wait_s, self.aside, self.target_aside
        c.outside, c.x, c.y, c.fx, c.fy, c.placed = self.outside, self.x, self.y, self.fx, self.fy, self.placed
        c.ahead = self.ahead[:]
        c.claims = [Claim(c, k.node, k.from_r, k.from_lane, k.to_r, k.to_lane, k.entered) for k in self.claims] \
            if self.claims else []
        return c

    def has_claim(self, to: Road) -> bool:
        for claim in self.claims:
            if claim.to_r is to and not claim.entered:
                return True
        return False

    def has_claim_from(self, road: Road) -> bool:
        for claim in self.claims:
            if claim.from_r is road and not claim.entered:
                return True
        return False


class ShuttleRoute:
    """A shuttle's route as it drives it: its points, how far along each is, and its bends."""

    __slots__ = ("points", "along", "length", "closed", "bends", "sig")

    def __init__(self, source):
        points = []
        for point in source:
            if not points or (point[0] - points[-1][0]) ** 2 + (point[1] - points[-1][1]) ** 2 > 1e-4:
                points.append((float(point[0]), float(point[1])))
        if len(points) < 2:
            points = [points[0], points[0]] if points else [(0.0, 0.0), (0.0, 0.0)]
        along = [0.0]
        for a, b in zip(points, points[1:]):
            along.append(along[-1] + math.dist(a, b))
        self.points, self.along, self.length = points, along, along[-1]
        self.closed = self.length > 0 and math.dist(points[0], points[-1]) < 1.0
        # Tells this geometry from another a route of the same name had, in a checkpoint as well
        self.sig = f"{len(points)}:{self.length!r}:{points[0]!r}:{points[-1]!r}"
        self.bends: list[tuple[float, float]] = []
        for i in range(1, len(points) - 1):
            ax, ay = self.point(along[i] - SHUTTLE_BEND_SPAN_M)
            bx, by = self.point(along[i])
            cx, cy = self.point(along[i] + SHUTTLE_BEND_SPAN_M)
            degrees = angle_deg(bx - ax, by - ay, cx - bx, cy - by)
            if degrees >= SHUTTLE_BEND_DEG:
                self.bends.append((along[i], degrees))

    def point(self, along: float) -> tuple[float, float]:
        if self.length <= 0:
            return self.points[0]
        along = along % self.length if self.closed else min(max(along, 0.0), self.length)
        index = bisect.bisect_left(self.along, along)
        if index < len(self.along) and self.along[index] == along:
            return self.points[index]
        index = min(max(index, 1), len(self.points) - 1)
        start = self.along[index - 1]
        t = (along - start) / (self.along[index] - start)
        (ax, ay), (bx, by) = self.points[index - 1], self.points[index]
        return ax + (bx - ax) * t, ay + (by - ay) * t

    def direction(self, along: float) -> tuple[float, float]:
        """The way the route runs here, looking a little either side so a corner turns smoothly."""
        if self.closed:
            behind, ahead = along - SHUTTLE_FACING_SPAN_M, along + SHUTTLE_FACING_SPAN_M
        else:
            behind, ahead = max(0.0, along - SHUTTLE_FACING_SPAN_M), min(self.length, along + SHUTTLE_FACING_SPAN_M)
        (ax, ay), (bx, by) = self.point(behind), self.point(ahead)
        length = math.hypot(bx - ax, by - ay)
        if length > 1e-3:
            return (bx - ax) / length, (by - ay) / length
        (ax, ay), (bx, by) = self.points[0], self.points[1]
        length = math.hypot(bx - ax, by - ay)
        return ((bx - ax) / length, (by - ay) / length) if length > 0 else (0.0, 1.0)

    def ahead(self, at: float, along: float) -> float:
        """How far ahead of along a place on the route is, the next time round on a loop."""
        ahead = at - along
        if self.closed and ahead < -self.length / 2:
            ahead += self.length
        return ahead

    def reach(self, along: float) -> tuple[float, float]:
        """The point this far along, carried on straight past the ends of an open route, the way the
        route runs over its first or last few metres: an end segment may be a sliver pointing anywhere,
        as where a depot's yard path meets its road, and would swing a body's end across the road."""
        if self.closed or 0.0 <= along <= self.length:
            return self.point(along)
        if along < 0:
            (x, y), (hx, hy), over = self.points[0], self.direction(0.0), along
        else:
            (x, y), (hx, hy), over = self.points[-1], self.direction(self.length), along - self.length
        return x + hx * over, y + hy * over

    def body(self, along: float, aside: float, half_length: float) -> tuple[float, float, float, float]:
        """Where a body this long stands with its middle this far along and this far left of the route:
        its middle and the way it faces. Its front and back are both on the route, as a bus's wheels
        follow its driver, so on a bend its back keeps inside the turn instead of swinging out."""
        hx, hy = self.direction(along + half_length)
        fx, fy = self.reach(along + half_length)
        fx, fy = fx - hy * aside, fy + hx * aside
        hx, hy = self.direction(along - half_length)
        bx, by = self.reach(along - half_length)
        bx, by = bx - hy * aside, by + hx * aside
        length = math.hypot(fx - bx, fy - by)
        if length < 1e-6:
            hx, hy = self.direction(along)
            x, y = self.point(along)
            return x - hy * aside, y + hx * aside, hx, hy
        return (fx + bx) / 2, (fy + by) / 2, (fx - bx) / length, (fy - by) / length


class Shuttle:
    """A shuttle as the traffic sees it: its body where the session put it, and how it is driving."""

    __slots__ = ("id", "x", "y", "fx", "fy", "half_length", "half_width", "speed", "driven", "gives_way_to",
                 "wait_s", "stops_within_m", "in_traffic", "aside", "target_aside", "placed", "route", "route_sig", "along",
                 "ahead_cache")

    def __init__(self, shuttle_id: str, half_length: float, half_width: float):
        self.id, self.half_length, self.half_width = shuttle_id, half_length, half_width
        self.x = self.y = self.fx = 0.0
        self.fy = 1.0
        self.speed = self.wait_s = self.aside = self.target_aside = self.along = 0.0
        self.stops_within_m = NO_LIMIT
        self.driven = self.in_traffic = self.placed = False
        self.gives_way_to: set[int] = set()
        self.route = None
        self.route_sig = ""
        self.ahead_cache = None

    def clone(self) -> Shuttle:
        copy = Shuttle.__new__(Shuttle)
        for name in Shuttle.__slots__:
            setattr(copy, name, getattr(self, name))
        copy.gives_way_to = set(self.gives_way_to)
        copy.ahead_cache = None
        return copy


class ShuttlePlan:
    """What the road ahead of a shuttle allows it this step: the speed the traffic in its way allows
    (limit_mps) and how much further it may go before it would touch any of it (cap_m), whether that
    traffic holds it up (held), and the speed the bends ahead allow (bend_mps)."""

    __slots__ = ("limit_mps", "cap_m", "held", "bend_mps")

    def __init__(self):
        self.limit_mps = self.cap_m = self.bend_mps = math.inf
        self.held = False


class CityTraffic:
    """One session's background traffic, and its shuttles as the traffic sees them."""

    def __init__(self, network: RoadNetwork):
        self.network = network
        self.time_s = 0.0
        self.vehicles: list[Vehicle] = []
        self.shuttles: dict[str, Shuttle] = {}
        # Road index -> (from, to) spans a hazard closes, in the order they were found
        self.hazards: dict[int, list[tuple[float, float]]] = {}
        # World events played out in the traffic: the vehicles each pins where it happened (event key ->
        # (their slots, when it clears)), and the junctions whose lights are stuck on red (junction id ->
        # when it clears). Both clear on the traffic's own clock, so a checkpoint replays them exactly.
        self.pins: dict[str, tuple[tuple[int, ...], float]] = {}
        self.signal_faults: dict[str, float] = {}
        self._pinned: set[int] = set()
        self.red_light_runs = 0
        self._claims_by_node: dict[int, list[Claim]] = {}
        self._lanes: dict[int, list[list[Vehicle]]] = {}
        self._filled: list[int] = []
        self._requests: dict[int, list[tuple]] = {}
        self._asked: list[int] = []
        self._look: list[tuple[float, float]] = []
        self._bodies: list[tuple] = []
        self._shuttle_routes: dict[str, tuple] = {}
        # False while the vehicles (and their claims) are shared with a copy: see _own_vehicles
        self._owns_vehicles = True

    # ---------------------------------------------------------------- building and copying

    @classmethod
    def start(cls, area: str) -> CityTraffic | None:
        """The area's sample, where its prepared file places it, or None with no prepared file."""
        network = road_network(area)
        return None if network is None else cls.on(network)

    @classmethod
    def on(cls, network: RoadNetwork) -> CityTraffic:
        """A network's sample, where its seeds place it."""
        traffic = cls(network)
        for slot, seed in enumerate(network.seeds):
            traffic._add(slot, seed)
        for vehicle in traffic.vehicles:
            traffic._pose(vehicle)
        return traffic

    def _add(self, slot: int, seed: dict) -> None:
        model = self.network.models.get(seed.get("model") or "")
        if model is None:
            return
        road = self.network.by_key.get(f"{seed.get('road')}|{seed.get('direction', 1)}")
        if road is None:
            road = next((r for r in self.network.roads if r.road_id == seed.get("road")), None)
        if road is None:
            return
        vehicle = Vehicle.__new__(Vehicle)
        vehicle.slot = len(self.vehicles)
        vehicle.seed = slot
        vehicle.id = seed.get("id") or f"{seed['model']}-{len(self.vehicles) + 1}"
        vehicle.model = seed["model"]
        vehicle.length = float(model["length_m"])
        vehicle.half = vehicle.length / 2
        vehicle.half_width = float(model["width_m"]) / 2
        vehicle.large = vehicle.model in ("bus", "truck", "heavy_truck")
        vehicle.accel = 4.0 if vehicle.model == "motorcycle" else 1.5 if vehicle.model == "heavy_truck" else \
            2.0 if vehicle.model in ("truck", "bus") else 3.0
        vehicle.rng = fnv_hash(vehicle.id)
        vehicle.speed_factor = 0.85 + 0.2 * random01(vehicle.rng)
        vehicle.road = road
        vehicle.lane = road.lane_from(int(seed.get("lane") or 0))
        vehicle.lane_index = 0
        vehicle.distance = min(max(float(seed.get("distance_m") or 0.0), 0.0), road.length)
        speed_kph = seed.get("speed_kph")
        vehicle.speed = max(0.0, float(40 if speed_kph is None else speed_kph) / 3.6 * 0.5)
        vehicle.target_speed = vehicle.stopped_s = vehicle.wait_s = vehicle.aside = vehicle.target_aside = 0.0
        vehicle.outside = False
        vehicle.ahead, vehicle.claims = [], []
        vehicle.x = vehicle.y = vehicle.fx = vehicle.fy = 0.0
        vehicle.placed = False
        self.vehicles.append(vehicle)

    def __deepcopy__(self, memo) -> CityTraffic:
        """A staged session copies its actor every tick, so this copies only what changes.

        The vehicles change only when the traffic steps, every TRAFFIC_STEP_TICKS ticks, or takes new
        hazards: until then the copy and the original share them, and whichever changes them first takes
        its own copy (_own_vehicles). A tick the traffic does not step clones none of them."""
        copy = CityTraffic.__new__(CityTraffic)
        copy.__dict__.update(self.__dict__)
        self._owns_vehicles = copy._owns_vehicles = False
        copy.shuttles = {key: shuttle.clone() for key, shuttle in self.shuttles.items()}
        copy.hazards = {road: spans[:] for road, spans in self.hazards.items()}
        copy.pins, copy.signal_faults, copy._pinned = dict(self.pins), dict(self.signal_faults), set(self._pinned)
        copy._lanes, copy._filled, copy._requests, copy._asked, copy._look, copy._bodies = {}, [], {}, [], [], []
        # Keyed by the actor's own waypoint lists, which the same deepcopy copies: keep them the same objects
        copy._shuttle_routes = {key: (deepcopy(waypoints, memo), route)
                                for key, (waypoints, route) in self._shuttle_routes.items()}
        memo[id(self)] = copy
        return copy

    def _own_vehicles(self) -> None:
        """Before anything changes a vehicle: vehicles shared with a copy are cloned first, so a staged
        copy never changes the traffic it was taken from, nor that traffic its copies. Their junction
        claims are gathered again in the vehicles' order, as a copy always did."""
        if self._owns_vehicles:
            return
        self.vehicles = [vehicle.clone() for vehicle in self.vehicles]
        self._claims_by_node = {}
        for vehicle in self.vehicles:
            for claim in vehicle.claims:
                self._claims_by_node.setdefault(claim.node, []).append(claim)
        # The lanes list the vehicles that were shared
        self._lanes, self._filled = {}, []
        self._owns_vehicles = True

    def state(self) -> dict:
        """Everything a checkpoint needs to carry on exactly as this would."""
        return {
            "area": self.network.area, "time_s": self.time_s, "red_light_runs": self.red_light_runs,
            "vehicles": [[v.road.index, v.lane, v.distance, v.speed, v.stopped_s, v.wait_s, v.aside, int(v.outside),
                          v.rng, [road.index for road in v.ahead],
                          [[c.node, c.from_r.index, c.from_lane, c.to_r.index, c.to_lane, int(c.entered)] for c in v.claims]]
                         for v in self.vehicles],
            "shuttles": [[s.id, s.x, s.y, s.fx, s.fy, s.half_length, s.half_width, s.speed, int(s.driven),
                          sorted(s.gives_way_to), s.wait_s, s.stops_within_m, int(s.in_traffic), s.aside,
                          s.target_aside, int(s.placed), s.along, s.route_sig] for s in self.shuttles.values()],
            "hazards": [[road, [list(span) for span in spans]] for road, spans in self.hazards.items()],
            "pins": [[key, list(slots), until] for key, (slots, until) in self.pins.items()],
            "signal_faults": [[junction, until] for junction, until in self.signal_faults.items()],
        }

    @classmethod
    def from_state(cls, state: dict, network: RoadNetwork | None = None) -> CityTraffic:
        """Traffic as state() left it, on its area's network or the one given."""
        traffic = cls.on(network) if network is not None else cls.start(state["area"])
        if traffic is None or len(state["vehicles"]) != len(traffic.vehicles):
            raise ValueError("Traffic checkpoint does not match the area's sample")
        roads = traffic.network.roads
        traffic.time_s = float(state["time_s"])
        traffic.red_light_runs = int(state["red_light_runs"])
        for vehicle, item in zip(traffic.vehicles, state["vehicles"]):
            road, lane, distance, speed, stopped_s, wait_s, aside, outside, rng, ahead, claims = item
            vehicle.road, vehicle.lane = roads[road], lane
            vehicle.distance, vehicle.speed, vehicle.stopped_s, vehicle.wait_s, vehicle.aside = \
                float(distance), float(speed), float(stopped_s), float(wait_s), float(aside)
            vehicle.outside, vehicle.rng = bool(outside), int(rng)
            vehicle.ahead = [roads[index] for index in ahead]
            vehicle.claims = [Claim(vehicle, node, roads[from_r], from_lane, roads[to_r], to_lane, bool(entered))
                              for node, from_r, from_lane, to_r, to_lane, entered in claims]
            for claim in vehicle.claims:
                traffic._claims_by_node.setdefault(claim.node, []).append(claim)
            traffic._pose(vehicle)
        for item in state["shuttles"]:
            (shuttle_id, x, y, fx, fy, half_length, half_width, speed, driven, gives_way_to, wait_s, stops_within,
             in_traffic, aside, target_aside, placed, along, route_sig) = item
            shuttle = traffic.shuttles[shuttle_id] = Shuttle(shuttle_id, float(half_length), float(half_width))
            shuttle.x, shuttle.y, shuttle.fx, shuttle.fy = float(x), float(y), float(fx), float(fy)
            shuttle.speed, shuttle.driven, shuttle.wait_s = float(speed), bool(driven), float(wait_s)
            shuttle.gives_way_to = set(gives_way_to)
            shuttle.stops_within_m, shuttle.in_traffic = float(stops_within), bool(in_traffic)
            shuttle.aside, shuttle.target_aside, shuttle.placed, shuttle.along = \
                float(aside), float(target_aside), bool(placed), float(along)
            shuttle.route_sig = route_sig
        traffic.hazards = {int(road): [tuple(span) for span in spans] for road, spans in state["hazards"]}
        # A checkpoint from before world events were played out in the traffic has neither
        count = len(traffic.vehicles)
        for key, slots, until in state.get("pins", ()):
            if not isinstance(key, str) or not all(type(slot) is int and 0 <= slot < count for slot in slots):
                raise ValueError("Traffic checkpoint pins a vehicle it does not have")
            traffic.pins[key] = (tuple(slots), float(until))
            traffic._pinned.update(slots)
        traffic.signal_faults = {str(junction): float(until) for junction, until in state.get("signal_faults", ())}
        return traffic

    # ---------------------------------------------------------------- hazards

    def set_hazards(self, hazards: list[tuple[list[tuple[float, float]], bool]]) -> None:
        """Replaces what traffic must keep off: (line, both ways) pairs, a one-way line running the way
        the traffic it holds up goes. Vehicles change their minds about roads they had not yet reached."""
        self._own_vehicles()
        found: dict[int, list[tuple[float, float]]] = {}
        for path, both_ways in hazards:
            if len(path) < 2:
                continue
            overlaps: dict[Road, tuple[float, float]] = {}
            length = line_length(path)
            along = 0.0
            while True:
                x, y, hx, hy = line_at(path, along)
                for road in self.network.near(x, y):
                    distance, at, tx, ty = road.project(x, y)
                    if distance > HAZARD_MATCH_M:
                        continue
                    alike = tx * hx + ty * hy
                    if (abs(alike) if both_ways else alike) < HAZARD_PARALLEL:
                        continue
                    span = overlaps.get(road)
                    overlaps[road] = (min(span[0], at), max(span[1], at)) if span else (at, at)
                if along >= length:
                    break
                along = min(along + HAZARD_STEP_M, length)
            for road, (start, end) in overlaps.items():
                # Where a road only touches the line, as at a junction, it is not on it
                if end - start < min(HAZARD_MIN_OVERLAP_M, min(road.length * 0.5, length * 0.8)):
                    continue
                if start < HAZARD_SNAP_M:
                    start = 0.0
                if road.length - end < HAZARD_SNAP_M:
                    end = road.length
                found.setdefault(road.index, []).append((start, end))
        self.hazards = found
        for vehicle in self.vehicles:
            for i, road in enumerate(vehicle.ahead):
                if road.index in found and not vehicle.has_claim(road):
                    del vehicle.ahead[i:]
                    break

    # ---------------------------------------------------------------- world events

    def locate(self, points) -> tuple[Road, float] | None:
        """The road under the middle of a line (an operating-network edge's path), running its way, and
        how far along that road the middle is; None where no road of the traffic lies under it."""
        if len(points) < 2:
            return None
        x, y, hx, hy = line_at(points, line_length(points) / 2)
        road, _ = self.network.road_under(x, y, hx, hy, HAZARD_PARALLEL)
        if road is None:
            return None
        return road, road.project(x, y)[1]

    def pin_vehicle(self, key: str, road: Road, distance: float, lane: int, until_s: float, count: int = 1) -> bool:
        """Stops count vehicles dead in a lane of a road, the first with its middle this far along and
        each next one right behind it, until the traffic's clock reaches until_s: a breakdown (one) or a
        collision (two). Whatever queues behind them does so of its own accord, and other lanes pass.

        Each is the vehicle of that lane nearest its place, within PIN_PICK_M, else one waiting outside
        the area, else the first free one of the sample; any other vehicle under it leaves the area. When
        the event clears they are towed away: they leave the area, as a jammed vehicle does. Returns
        whether the event's vehicles are pinned: False, to try again later, while a shuttle stands where
        one would go. Pinning an event already pinned changes nothing."""
        if key in self.pins:
            return True
        if until_s <= self.time_s or count < 1:
            return False
        self._own_vehicles()
        self._fill_lanes()
        lane = road.lane_from(lane)
        queue = self._lane(road, lane)
        chosen: list[Vehicle] = []
        places: list[float] = []
        front = min(max(distance, 0.0), road.length)
        for _ in range(count):
            taken = self._pinned.union(v.slot for v in chosen)
            near = [v for v in queue if v.slot not in taken and abs(v.distance - front) <= PIN_PICK_M]
            pick = min(near, key=lambda v: (abs(v.distance - front), v.slot)) if near else next(
                (v for v in self.vehicles if v.outside and v.slot not in taken),
                next((v for v in self.vehicles if v.slot not in taken), None))
            if pick is None:
                return False
            if chosen:
                front -= chosen[-1].half + PIN_TOUCH_M + pick.half
            at = min(max(front, pick.half), road.length)
            x, y, fx, fy = road.sample(at, road.lane_offset(lane))
            for shuttle in self.shuttles.values():
                if shuttle.placed and overlap(shuttle.x, shuttle.y, shuttle.fx, shuttle.fy, shuttle.half_length + MIN_GAP_M,
                                              shuttle.half_width, x, y, fx, fy, pick.half, pick.half_width):
                    return False
            chosen.append(pick)
            places.append(at)
        for vehicle, at in zip(chosen, places):
            self._release_all_claims(vehicle)
            vehicle.ahead = []
            vehicle.outside = False
            vehicle.road, vehicle.lane, vehicle.distance = road, lane, at
            vehicle.speed = vehicle.target_speed = vehicle.stopped_s = vehicle.wait_s = 0.0
            vehicle.aside = vehicle.target_aside = 0.0
            self._pose(vehicle)
        slots = tuple(vehicle.slot for vehicle in chosen)
        self.pins[key] = (slots, float(until_s))
        self._pinned.update(slots)
        # Nothing else stands where they now do
        for vehicle in self.vehicles:
            if not vehicle.placed or vehicle.slot in self._pinned:
                continue
            for pinned in chosen:
                if (abs(vehicle.x - pinned.x) < SHUTTLE_BODY_REACH_M and abs(vehicle.y - pinned.y) < SHUTTLE_BODY_REACH_M
                        and overlap(pinned.x, pinned.y, pinned.fx, pinned.fy, pinned.half + MIN_GAP_M / 2, pinned.half_width,
                                    vehicle.x, vehicle.y, vehicle.fx, vehicle.fy, vehicle.half, vehicle.half_width)):
                    self._enter(vehicle)
                    self._pose(vehicle)
                    break
        return True

    def pin_until(self, key: str, until_s: float) -> None:
        """Moves when a pinned event clears, as when the operator's escalation brings help sooner."""
        known = self.pins.get(key)
        if known is not None and known[1] != until_s:
            self.pins[key] = (known[0], float(until_s))

    def set_signal_fault(self, junction_id: str, until_s: float) -> None:
        """Sticks a junction's lights on red on every approach until the traffic's clock reaches until_s
        (signal_cycle). Vehicles that hold the junction already drive on through it; the rest queue, and
        the jam spills back as far as it goes. A later call moves when it clears."""
        if until_s > self.time_s:
            self.signal_faults[junction_id] = float(until_s)

    def _clear_events(self) -> None:
        """Ends the world events whose time is up: their pinned vehicles are towed away and their lights
        work again."""
        for key in [key for key, (_, until) in self.pins.items() if until <= self.time_s]:
            slots, _ = self.pins.pop(key)
            for slot in slots:
                self._pinned.discard(slot)
                vehicle = self.vehicles[slot]
                if not vehicle.outside:
                    self._enter(vehicle)
        for junction in [junction for junction, until in self.signal_faults.items() if until <= self.time_s]:
            del self.signal_faults[junction]

    def _fault(self, junction: str) -> float | None:
        """When a junction's fault clears, while its lights are stuck; else None."""
        until = self.signal_faults.get(junction) if self.signal_faults else None
        return until if until is not None and self.time_s < until else None

    # ---------------------------------------------------------------- driving

    def step(self, dt: float, time_s: float) -> None:
        """Drives every vehicle for dt seconds, the lights showing their colours at time_s."""
        self._own_vehicles()
        self.time_s = time_s
        # The shuttles' bodies, which no vehicle drives or comes in on top of this step
        self._bodies = [(s.x, s.y, s.fx, s.fy, s.half_length, s.half_width) for s in self.shuttles.values() if s.placed]
        self._fill_lanes()
        if self.pins or self.signal_faults:
            self._clear_events()
        # A pinned vehicle stands where its event happened, whatever closes the road around it
        pinned = self._pinned
        for vehicle in self.vehicles:
            if vehicle.outside or (self._caught(vehicle) and vehicle.slot not in pinned):
                self._enter(vehicle)
        self._clear_shuttle_jams()
        self._fill_lanes()
        for vehicle in self.vehicles:
            if vehicle.outside or (pinned and vehicle.slot in pinned):
                continue
            self._release_claims(vehicle)
            self._plan_ahead(vehicle)
            self._ask_for_junction(vehicle)
        self._answer_junctions(dt)
        for vehicle in self.vehicles:
            if not vehicle.outside:
                vehicle.target_speed = 0.0 if pinned and vehicle.slot in pinned else self._safe_speed(vehicle)
        for vehicle in self.vehicles:
            if vehicle.outside:
                continue
            if pinned and vehicle.slot in pinned:
                vehicle.speed = 0.0
                continue
            self._move(vehicle, dt)
        self._bodies = []
        for vehicle in self.vehicles:
            self._pose(vehicle)

    def _pose(self, vehicle: Vehicle) -> None:
        if vehicle.outside:
            vehicle.placed = False
            return
        road = vehicle.road
        vehicle.x, vehicle.y, vehicle.fx, vehicle.fy = road.sample(vehicle.distance, road.lane_offset(vehicle.lane) + vehicle.aside)
        vehicle.placed = True

    def _lane(self, road: Road, lane: int) -> list[Vehicle]:
        lanes = self._lanes.get(road.index)
        if lanes is None:
            lanes = self._lanes[road.index] = [[] for _ in range(road.lanes)]
        return lanes[lane]

    def _fill_lanes(self) -> None:
        """Each lane's vehicles, back to front."""
        for index in self._filled:
            for lane in self._lanes[index]:
                lane.clear()
        self._filled = []
        for vehicle in self.vehicles:
            if vehicle.outside:
                continue
            lanes = self._lanes.get(vehicle.road.index)
            if lanes is None:
                lanes = self._lanes[vehicle.road.index] = [[] for _ in range(vehicle.road.lanes)]
            if not any(lanes):
                self._filled.append(vehicle.road.index)
            lanes[vehicle.lane].append(vehicle)
        for index in self._filled:
            for lane in self._lanes[index]:
                if len(lane) > 1:
                    lane.sort(key=lambda v: (v.distance, v.id))
                for i, vehicle in enumerate(lane):
                    vehicle.lane_index = i

    def _caught(self, vehicle: Vehicle) -> bool:
        """A closure or a growing queue that reaches a vehicle takes it off the road: its place in the
        queue is one of the incident's own cars."""
        for start, end in self.hazards.get(vehicle.road.index, ()):
            if vehicle.distance + vehicle.half > start + STOP_MARGIN_M / 2 and vehicle.distance - vehicle.half < end:
                return True
        return False

    def _plan_ahead(self, vehicle: Vehicle) -> None:
        reach = vehicle.road.length - vehicle.distance
        for road in vehicle.ahead:
            reach += road.length
        while not vehicle.ahead or (reach < PLAN_AHEAD_M and len(vehicle.ahead) < MAX_PLANNED_ROADS):
            nxt = self._choose_next_road(vehicle, vehicle.ahead[-1] if vehicle.ahead else vehicle.road)
            if nxt is None:
                break
            vehicle.ahead.append(nxt)
            reach += nxt.length

    def _choose_next_road(self, vehicle: Vehicle, origin: Road) -> Road | None:
        exits = self.network.exits.get(origin.to_node)
        if not exits:
            return None
        # Large vehicles keep off service roads, the rule the sample places them by
        allowed = [road for road in exits if not (vehicle.large and road.service)]
        # A road with a hazard on it is taken only if there is no other way, even back
        clear = [road for road in allowed if road.index not in self.hazards]
        if clear:
            allowed = clear
        candidates = [road for road in allowed if road.road_id != origin.road_id] or allowed
        if not candidates:
            return None
        if len(candidates) == 1:
            return candidates[0]
        weights = [max(0.01, class_weight(road.cls) * lerp(0.4, 1.6, self.network.turn(origin, road)[0]))
                   for road in candidates]
        vehicle.rng = rng_step(vehicle.rng)
        pick = random01(vehicle.rng) * sum(weights)
        for road, weight in zip(candidates, weights):
            pick -= weight
            if pick <= 0:
                return road
        return candidates[-1]

    def _release_claims(self, vehicle: Vehicle) -> None:
        claims = vehicle.claims
        for i in range(len(claims) - 1, -1, -1):
            claim = claims[i]
            cleared = claim.entered and (vehicle.road is not claim.to_r or vehicle.distance - vehicle.half > CLEARED_JUNCTION_M)
            if not cleared and (claim.entered or vehicle.stopped_s < RELEASE_CLAIM_S):
                continue
            self._claims_by_node[claim.node].remove(claim)
            del claims[i]

    def _release_all_claims(self, vehicle: Vehicle) -> None:
        for claim in vehicle.claims:
            self._claims_by_node[claim.node].remove(claim)
        vehicle.claims = []

    def _must_stop(self, offset: int, phase: int, junction: str, front_to_line: float, speed: float) -> bool:
        """Whether a light tells a vehicle with its front this far short of its line to stop."""
        if front_to_line < -PAST_LINE_M:
            return False
        colour = light(self.time_s, offset, phase, self._fault(junction) if self.signal_faults else None)
        if colour == 0:
            return False
        # Too close to stop for a yellow: carry on through it
        return colour == 2 or speed * speed / (2 * YELLOW_BRAKE) <= front_to_line

    def _light_holds(self, vehicle: Vehicle, road: Road, road_start: float) -> bool:
        for at, offset, phase, junction in road.stops:
            if self._must_stop(offset, phase, junction, road_start + at - vehicle.half, vehicle.speed):
                return True
        return False

    def _ask_for_junction(self, vehicle: Vehicle) -> None:
        """Lets the next junction know this vehicle is coming, unless a light is holding it first."""
        road, lane, road_start = vehicle.road, vehicle.lane, -vehicle.distance
        speed = vehicle.speed
        reach = speed * speed / (2 * COMFORT_BRAKE) + speed * REACTION_S + CLAIM_MARGIN_M
        notice = max(reach, speed * NOTICE_S)
        for i, nxt in enumerate(vehicle.ahead):
            if road.stops and self._light_holds(vehicle, road, road_start):
                return
            next_lane = nxt.lane_from(lane)
            to_node = road_start + road.length
            if to_node - vehicle.half > notice:
                return
            if not vehicle.has_claim(nxt):
                # It cannot go before the vehicle in front of it, so it has nothing to ask yet
                if i == 0:
                    queue = self._lane(road, lane)
                    if vehicle.lane_index + 1 < len(queue) and not queue[vehicle.lane_index + 1].has_claim_from(road):
                        return
                node = road.to_node
                asked = self._requests.get(node)
                if asked is None:
                    asked = self._requests[node] = []
                if not asked:
                    self._asked.append(node)
                rank = road.rank + vehicle.wait_s / WAIT_PER_RANK_S
                # (rank, metres to the junction, vehicle, from, lane, to, lane, can take it, plan index)
                asked.append((rank, to_node, vehicle, road, lane, nxt, next_lane, to_node - vehicle.half <= reach, i))
                return
            road_start, road, lane = to_node, nxt, next_lane

    def _answer_junctions(self, dt: float) -> None:
        """Each junction gives way in order of priority. Whoever it cannot let go yet, or who is still
        coming, holds up anything lower that would cross their path, so a side road waits for a gap."""
        network = self.network
        for node in self._asked:
            asked = self._requests[node]
            if len(asked) > 1:
                asked.sort(key=lambda r: (-r[0], r[1], r[2].id))
            denied: list[tuple] = []
            claims = self._claims_by_node.get(node)
            for request in asked:
                _, _, vehicle, origin, lane, to, to_lane, can_take, plan_index = request
                movement = (origin.index, lane, to.index, to_lane)
                crossed = False
                if claims:
                    for claim in claims:
                        if claim.vehicle is not vehicle and network.conflict(movement, claim.movement):
                            crossed = True
                            break
                if not crossed:
                    for other in denied:
                        if network.conflict(movement, other):
                            crossed = True
                            break
                if (not crossed and can_take and not self._shuttle_holds_node(origin, vehicle)
                        and self._has_room_beyond(vehicle, to, to_lane, plan_index)):
                    if claims is None:
                        claims = self._claims_by_node[node] = []
                    claim = Claim(vehicle, node, origin, lane, to, to_lane)
                    claims.append(claim)
                    vehicle.claims.append(claim)
                    vehicle.wait_s = 0.0
                    continue
                if can_take:
                    vehicle.wait_s += dt
                # Stopped by a full road, or by a shuttle, it is not about to go: others may use the gap
                if crossed or not can_take:
                    denied.append(movement)
            asked.clear()
        self._asked = []

    def _shuttle_holds_node(self, arriving: Road, vehicle: Vehicle) -> bool:
        """A junction a shuttle is about to use stays shut. One posed off the roads cannot give way. One on
        them leaves the junction to the rest while it stops short of it, for a light or for traffic, until
        it has waited its turn, and never shuts it to a vehicle it is giving way to."""
        if not self.shuttles:
            return False
        nx, ny = arriving.xs[-1], arriving.ys[-1]
        for shuttle in self.shuttles.values():
            if vehicle.slot in shuttle.gives_way_to:
                continue
            moving = shuttle.speed > SHUTTLE_MOVING_MPS
            reach = SHUTTLE_JUNCTION_M if moving else STANDING_SHUTTLE_JUNCTION_M
            tx, ty = nx - shuttle.x, ny - shuttle.y
            if tx * tx + ty * ty > reach * reach:
                continue
            ahead = tx * shuttle.fx + ty * shuttle.fy
            if (shuttle.driven and shuttle.wait_s < SHUTTLE_TAKE_TURN_S
                    and ahead - shuttle.half_length > shuttle.stops_within_m):
                continue
            if not moving or ahead > -shuttle.half_length - CLEARED_JUNCTION_M:
                return True
        return False

    def _has_room_beyond(self, vehicle: Vehicle, road: Road, lane: int, index: int) -> bool:
        """Don't block the box: go only if there is room for the whole vehicle on the far side."""
        needed = vehicle.length + MIN_GAP_M
        passed = 0.0
        while True:
            for start, _ in self.hazards.get(road.index, ()):
                if passed + start < needed:
                    return False
            for other in self._lane(road, lane):
                if other is vehicle:
                    continue
                if other.speed < STANDING_MPS and passed + other.distance - other.half < needed:
                    return False
                break
            passed += road.length
            index += 1
            if passed >= needed or index >= len(vehicle.ahead):
                return True
            road = vehicle.ahead[index]
            lane = road.lane_from(lane)

    def _safe_speed(self, vehicle: Vehicle) -> float:
        speed = vehicle.speed
        limit = vehicle.road.speed * vehicle.speed_factor
        half = vehicle.half
        road, lane, road_start = vehicle.road, vehicle.lane, -vehicle.distance
        ahead = vehicle.ahead
        i = 0
        while True:
            queue = self._lane(road, lane)
            leader_index = vehicle.lane_index + 1 if i == 0 else 0
            leader = queue[leader_index] if leader_index < len(queue) else None
            if leader is vehicle:
                leader = queue[leader_index + 1] if leader_index + 1 < len(queue) else None
            if leader is not None:
                limit = min(limit, krauss(speed, road_start + leader.distance - leader.half - half - MIN_GAP_M, leader.speed))
            spans = self.hazards.get(road.index)
            if spans:
                for start, _ in spans:
                    if road_start + start > 0:
                        limit = min(limit, krauss(speed, road_start + start - half - STOP_MARGIN_M, 0.0))
            for at, offset, phase, junction in road.stops:
                if self._must_stop(offset, phase, junction, road_start + at - half, speed):
                    limit = min(limit, krauss(speed, road_start + at - half - STOP_MARGIN_M, 0.0))
            # On past any leader, which may turn off and leave this vehicle facing what lies beyond
            if i >= len(ahead):
                break
            nxt = ahead[i]
            to_node = road_start + road.length
            if not vehicle.has_claim(nxt):
                limit = min(limit, krauss(speed, to_node - half - NODE_STOP_BACK_M, 0.0))
                break
            # Slows for the turn in time to be at the turn's speed when it gets there
            full = road.speed * vehicle.speed_factor
            turn = lerp(3.0, full, self.network.turn(road, nxt)[1])
            limit = min(limit, math.sqrt(turn * turn + 2 * COMFORT_BRAKE * max(0.0, to_node - half)))
            road_start, road, lane = to_node, nxt, nxt.lane_from(lane)
            if road_start > PLAN_AHEAD_M:
                break
            i += 1
        return self._shuttle_limit(vehicle, limit) if self.shuttles else self._no_shuttles(vehicle, limit)

    @staticmethod
    def _no_shuttles(vehicle: Vehicle, limit: float) -> float:
        vehicle.target_aside = 0.0
        return limit

    def _shuttle_limit(self, vehicle: Vehicle, limit: float) -> float:
        """Traffic follows a shuttle going its way, or held up in traffic as it is, stops for one crossing,
        and otherwise pulls aside for it to pass or to get past it. One on the roads keeps to its lane and
        follows the traffic, so nobody pulls aside for it from in front, and one it gives way to goes first."""
        vehicle.target_aside = 0.0
        if not vehicle.placed:
            return limit
        half = vehicle.half
        fx, fy = vehicle.fx, vehicle.fy
        # It can move this far within its own lane without slowing
        in_lane = max(0.0, LANE_WIDTH_M / 2 - vehicle.half_width)
        for shuttle in self.shuttles.values():
            if vehicle.slot in shuttle.gives_way_to:
                continue
            tx, ty = shuttle.x - vehicle.x, shuttle.y - vehicle.y
            if tx * tx + ty * ty > SHUTTLE_SIGHT_M * SHUTTLE_SIGHT_M:
                continue
            ahead = tx * fx + ty * fy
            side = tx * -fy + ty * fx
            clearance = shuttle.half_width + vehicle.half_width + SHUTTLE_CLEARANCE_M
            gap = ahead - shuttle.half_length - half - MIN_GAP_M
            alike = shuttle.fx * fx + shuttle.fy * fy
            moving = shuttle.speed > SHUTTLE_MOVING_MPS
            if abs(alike) < 0.5:
                if ahead > 0 and abs(side) < clearance + shuttle.half_length:
                    limit = min(limit, krauss(vehicle.speed, gap, 0.0))
                continue
            if (moving or shuttle.in_traffic) and alike > 0 and ahead > 0:
                # Further back than behind a car: a bus's back swings across as it turns
                if abs(side) < clearance:
                    limit = min(limit, krauss(vehicle.speed, gap - SHUTTLE_SWING_M, shuttle.speed * alike))
                continue
            # A shuttle on the roads behind follows this vehicle, as the traffic does
            if shuttle.driven and alike > 0 and ahead < 0:
                continue
            # What is left is passing: one coming the other way, one standing ahead, or one catching up from behind
            if ahead < -shuttle.half_length - half - SHUTTLE_PASS_BEHIND_M or ahead > SHUTTLE_PASS_AHEAD_M:
                continue
            if moving and alike > 0 and shuttle.speed <= vehicle.speed:
                continue
            lane_side = side + vehicle.aside
            aside = max(0.0, clearance + lane_side) if lane_side <= 0 else -max(0.0, clearance - lane_side)
            if aside == 0:
                continue
            if abs(aside) > abs(vehicle.target_aside):
                vehicle.target_aside = min(max(aside, -MAX_ASIDE_M), MAX_ASIDE_M)
            if ahead > 0 and abs(aside) > in_lane:
                limit = min(limit, PASSING_MPS)
            # Not yet out of its way: wait where it can still get there
            if ahead > 0 and abs(side) < clearance - SHUTTLE_BESIDE_M:
                limit = min(limit, krauss(vehicle.speed, gap, 0.0))
        return limit

    def _move(self, vehicle: Vehicle, dt: float) -> None:
        rate = HARD_BRAKE if vehicle.target_speed < vehicle.speed else vehicle.accel
        vehicle.speed = move_towards(vehicle.speed, vehicle.target_speed, rate * dt)
        aside = vehicle.aside
        vehicle.aside = move_towards(vehicle.aside, vehicle.target_aside, ASIDE_MPS * dt)
        # Whatever the rules allowed, a move along or across the road that would put it into a shuttle's
        # body is not made
        if self._bodies and self._would_hit_shuttle(vehicle, vehicle.speed * dt):
            vehicle.speed = 0.0
            if self._would_hit_shuttle(vehicle, 0.0):
                vehicle.aside = aside
        vehicle.stopped_s = vehicle.stopped_s + dt if vehicle.speed < STOPPED_MPS else 0.0
        if vehicle.stopped_s > STUCK_S:
            self._enter(vehicle)
            return
        before = vehicle.distance
        vehicle.distance += vehicle.speed * dt
        for at, offset, phase, junction in vehicle.road.stops:
            # A fault that struck too late to stop for, with no yellow first, is not running a red
            if (before + vehicle.half <= at < vehicle.distance + vehicle.half
                    and light(self.time_s, offset, phase) == 2 and not (self.signal_faults and self._fault(junction))):
                self.red_light_runs += 1
        safety = 0
        while vehicle.distance > vehicle.road.length and safety < 6:
            safety += 1
            if not vehicle.ahead:
                self._enter(vehicle)
                return
            vehicle.distance -= vehicle.road.length
            nxt = vehicle.ahead.pop(0)
            for claim in vehicle.claims:
                if claim.to_r is nxt:
                    claim.entered = True
            vehicle.road, vehicle.lane = nxt, nxt.lane_from(vehicle.lane)

    def _would_hit_shuttle(self, vehicle: Vehicle, step_m: float) -> bool:
        x, y = vehicle.x, vehicle.y
        future = None
        for body in self._bodies:
            if abs(body[0] - x) > SHUTTLE_BODY_REACH_M or abs(body[1] - y) > SHUTTLE_BODY_REACH_M:
                continue
            if future is None:
                future = self._future(vehicle, step_m)
            if overlap(*body, *future, vehicle.half, vehicle.half_width) and not (
                    vehicle.placed and overlap(*body, x, y, vehicle.fx, vehicle.fy, vehicle.half, vehicle.half_width)):
                return True
        return False

    def _enter(self, vehicle: Vehicle) -> None:
        """A road with no way on leaves the area, so the vehicle has gone: another comes in at the edge.
        Otherwise those roads would collect the traffic until the rest of the city ran empty. With no room
        on any road in, it waits outside for one."""
        self._release_all_claims(vehicle)
        vehicle.ahead = []
        vehicle.stopped_s = vehicle.wait_s = vehicle.aside = 0.0
        vehicle.placed = False
        vehicle.outside = True
        entries = self.network.large_entries if vehicle.large else self.network.entries
        if not entries:
            entries = self.network.entries
        if not entries:
            return
        vehicle.rng = rng_step(vehicle.rng)
        count = len(entries)
        for i in range(min(count, 8)):
            road = entries[((vehicle.rng + i) & 0xFFFFFFFF) % count]
            if road.index in self.hazards or not self._room_at_start(road, road.lane_from(vehicle.lane), vehicle.length):
                continue
            vehicle.outside = False
            vehicle.distance = 0.0
            vehicle.speed = 0.0 if road.stops else min(vehicle.speed, ENTER_MPS)
            vehicle.road, vehicle.lane = road, road.lane_from(vehicle.lane)
            queue = self._lane(road, vehicle.lane)
            if road.index not in self._filled:
                self._filled.append(road.index)
            queue.insert(0, vehicle)
            return

    def _room_at_start(self, road: Road, lane: int, length: float) -> bool:
        # A road in may still have its own other direction turning back onto it
        for claim in self._claims_by_node.get(road.from_node, ()):
            if claim.to_r is road:
                return False
        for other in self._lane(road, lane):
            if other.road is road and other.distance - other.half < length + MIN_GAP_M:
                return False
        if self._bodies:
            # Nor may it come in on top of a shuttle
            x, y, fx, fy = road.sample(length / 2, road.lane_offset(lane))
            for body in self._bodies:
                if (abs(body[0] - x) < SHUTTLE_BODY_REACH_M and abs(body[1] - y) < SHUTTLE_BODY_REACH_M
                        and overlap(*body, x, y, fx, fy, length / 2 + MIN_GAP_M, LANE_WIDTH_M / 2)):
                    return False
        return True

    def _clear_shuttle_jams(self) -> None:
        """A shuttle kept waiting so long that what it waits for must be stuck has that traffic leave the
        area, as a vehicle stuck in a jam does. A vehicle a world event pinned is the event itself, not a
        jam: it stays until the event clears, and the shuttle waits behind it (pin_vehicle)."""
        pinned = self._pinned
        for shuttle in self.shuttles.values():
            if not shuttle.driven or shuttle.wait_s < SHUTTLE_GIVE_UP_S:
                continue
            for slot in sorted(shuttle.gives_way_to):
                vehicle = self.vehicles[slot]
                if not vehicle.outside and vehicle.speed < STOPPED_MPS and slot not in pinned:
                    self._enter(vehicle)
            # And whatever stands where its body would go next
            for _ in range(4):
                if shuttle.route is None:
                    break
                blocker = self.body_blocker(shuttle, shuttle.route, shuttle.along + SHUTTLE_LOOK_STEP_M)
                if not isinstance(blocker, Vehicle) or blocker.speed >= STOPPED_MPS or blocker.slot in pinned:
                    break
                self._enter(blocker)
                self._pose(blocker)
            shuttle.gives_way_to.clear()
            shuttle.wait_s = 0.0

    # ---------------------------------------------------------------- shuttles

    def shuttle_route(self, key: str, waypoints) -> ShuttleRoute:
        """A route's driving geometry, worked out again only when its waypoints change."""
        known = self._shuttle_routes.get(key)
        if known is None or known[0] is not waypoints:
            known = self._shuttle_routes[key] = (waypoints, ShuttleRoute([(p.x, p.y) for p in waypoints]))
        return known[1]

    def shuttle(self, shuttle_id: str, half_length: float, half_width: float) -> Shuttle:
        shuttle = self.shuttles.get(shuttle_id)
        if shuttle is None:
            shuttle = self.shuttles[shuttle_id] = Shuttle(shuttle_id, half_length, half_width)
        return shuttle

    def plan_shuttle(self, shuttle: Shuttle, route: ShuttleRoute, along: float, cruise: float) -> ShuttlePlan:
        """How fast the shuttle may go for what is in its way along its route, and how much further it
        may go at most before it touches any of it. It follows a vehicle going its way and stops for one
        standing or crossing in its path, which it then gives way to; of two shuttles crossing, the later
        by name waits. The lane it means to drive in is chosen here as well."""
        speed = shuttle.speed
        heading = route.direction(along)
        px, py = route.point(along)
        _, shuttle.target_aside = self.network.road_under(px, py, heading[0], heading[1], SHUTTLE_ROAD_ALIGN)
        plan = ShuttlePlan()
        for at, degrees in route.bends:
            to_bend = route.ahead(at, along)
            if 0 <= to_bend <= PLAN_AHEAD_M:
                turn = lerp(3.0, cruise, inverse_lerp(120.0, 15.0, degrees))
                plan.bend_mps = min(plan.bend_mps, math.sqrt(turn * turn + 2 * COMFORT_BRAKE * to_bend))
        shuttle.gives_way_to.clear()
        look = max(SHUTTLE_LOOK_M, speed * speed / (2 * COMFORT_BRAKE) + speed * REACTION_S + CLAIM_MARGIN_M)
        if not route.closed:
            look = min(look, route.length - along)
        # The path its body sweeps, through its own lane
        points = self._look
        points.clear()
        d = 0.0
        while True:
            hx, hy = route.direction(along + d)
            x, y = route.point(along + d)
            points.append((x - hy * shuttle.aside, y + hx * shuttle.aside))
            if d >= look:
                break
            d = min(d + SHUTTLE_LOOK_STEP_M, look)
        stops_within = NO_LIMIT
        pinned = self._pinned
        if len(points) >= 2:
            reach = look + SHUTTLE_LOOK_STEP_M + shuttle.half_length
            ox, oy = shuttle.x, shuttle.y
            for vehicle in self.vehicles:
                if not vehicle.placed:
                    continue
                ax, ay = vehicle.x, vehicle.y
                if (ax - ox) ** 2 + (ay - oy) ** 2 > reach * reach:
                    continue
                found, along_m, side, tx, ty = self._along_look(ax, ay)
                if not found:
                    continue
                facing_x, facing_y = vehicle.fx, vehicle.fy
                alike = facing_x * tx + facing_y * ty
                # One given way at a junction, coming across, is watched for where it is going, along the
                # roads it means to take, not only where it is
                if (abs(alike) <= 0.5 and vehicle.claims and vehicle.speed > STANDING_MPS
                        and (along_m <= 0 or side >= shuttle.half_width + vehicle.half + SHUTTLE_SIDE_ROOM_M)):
                    crosses, along_m, side, tx, ty, facing_x, facing_y = self._crosses_plan(shuttle, vehicle)
                    alike = facing_x * tx + facing_y * ty
                if along_m <= 0:
                    continue
                across = abs(facing_x * ty - facing_y * tx)
                if pinned and vehicle.slot in pinned and side >= (shuttle.half_width + across * vehicle.half
                                                                  + abs(alike) * vehicle.half_width + SHUTTLE_ONCOMING_ROOM_M):
                    # A breakdown or a wreck beside its way, not in it: it squeezes past at a crawl
                    to_it = max(0.0, along_m - vehicle.half - shuttle.half_length)
                    plan.limit_mps = min(plan.limit_mps, math.sqrt(PINNED_PASS_MPS * PINNED_PASS_MPS + 2 * COMFORT_BRAKE * to_it))
                    continue
                room = SHUTTLE_ONCOMING_ROOM_M if alike < -0.5 else SHUTTLE_SIDE_ROOM_M
                if side >= shuttle.half_width + across * vehicle.half + abs(alike) * vehicle.half_width + room:
                    continue
                # One whose own way runs into the shuttle where it stands cannot go first: it waits for
                # the shuttle, which never drives into it
                if self._runs_into(vehicle, shuttle):
                    continue
                gap = self._gap(shuttle, plan, along_m, side, tx, ty, facing_x, facing_y, alike, vehicle.half,
                                vehicle.half_width, vehicle.speed, cruise)
                if gap is None:
                    continue
                if max(0.0, vehicle.speed * alike) >= STANDING_MPS:
                    continue
                # It waits for this one, which may go first even where it would otherwise wait for the shuttle
                shuttle.gives_way_to.add(vehicle.slot)
                stops_within = min(stops_within, max(0.0, gap))
            for other in self.shuttles.values():
                if other is shuttle or not other.placed:
                    continue
                ax, ay = other.x, other.y
                if (ax - ox) ** 2 + (ay - oy) ** 2 > (reach + SHUTTLE_CROSS_NOTICE_M) ** 2:
                    continue
                found, along_m, side, tx, ty = self._along_look(ax, ay)
                if not found:
                    continue
                alike = other.fx * tx + other.fy * ty
                if alike <= 0.5 and other.driven:
                    first = shuttle.id < other.id
                    if first and (other.speed < SHUTTLE_MOVING_MPS or along_m <= 0
                                  or side >= shuttle.half_width + other.half_length + SHUTTLE_SIDE_ROOM_M):
                        continue
                    if not first:
                        crosses, along_m, side, tx, ty = self._crosses_look(shuttle, ax, ay, other.fx, other.fy, other.speed,
                                                                             other.half_length, other.half_width)
                        if not crosses:
                            continue
                    alike = other.fx * tx + other.fy * ty
                if along_m <= 0:
                    continue
                # Two buses round a bend touch at their corners before their middles come close: a
                # following shuttle keeps further back
                gap = self._gap(shuttle, plan, along_m - SHUTTLE_FOLLOW_EXTRA_M, side, tx, ty, other.fx, other.fy, alike,
                                other.half_length, other.half_width, other.speed, cruise)
                if gap is not None and max(0.0, other.speed * alike) < STANDING_MPS:
                    stops_within = min(stops_within, max(0.0, gap))
        shuttle.stops_within_m = stops_within
        return plan

    def _gap(self, shuttle, plan, along_m, side, tx, ty, facing_x, facing_y, alike, half_length, half_width, speed,
             cruise) -> float | None:
        """Holds the shuttle for one body beside its path ahead: None if it is clear of the path, else how
        far the shuttle's middle may go before it is too close to it."""
        across = abs(facing_x * ty - facing_y * tx)
        side_reach = across * half_length + abs(alike) * half_width
        room = SHUTTLE_ONCOMING_ROOM_M if alike < -0.5 else SHUTTLE_SIDE_ROOM_M
        if side >= shuttle.half_width + side_reach + room:
            return None
        gap = along_m - abs(alike) * half_length - across * half_width - shuttle.half_length - MIN_GAP_M
        limit = krauss(shuttle.speed, gap, max(0.0, speed * alike))
        if limit < cruise:
            plan.held = True
        plan.limit_mps = min(plan.limit_mps, limit)
        plan.cap_m = min(plan.cap_m, max(0.0, gap))
        return gap

    def _crosses_look(self, shuttle, ax, ay, facing_x, facing_y, speed, half_length, half_width):
        """The first place, from where a body is now to where it will be a little later, that is in the
        shuttle's path ahead, as a driver watches one coming across."""
        notice = min(SHUTTLE_CROSS_NOTICE_M, speed * SHUTTLE_CROSS_NOTICE_S) + half_length
        d = 0.0
        while True:
            found, along_m, side, tx, ty = self._along_look(ax + facing_x * d, ay + facing_y * d)
            if found and along_m > 0 and side < shuttle.half_width + half_width + SHUTTLE_SIDE_ROOM_M:
                return True, along_m, side, tx, ty
            if d >= notice:
                return (False, *self._along_look(ax, ay)[1:])
            d = min(d + SHUTTLE_LOOK_STEP_M, notice)

    def _crosses_plan(self, shuttle, vehicle: Vehicle):
        """The first place a vehicle will be in the shuttle's path ahead, along the roads it means to
        take, within a little time at its speed: as _crosses_look, but following its turns."""
        notice = min(SHUTTLE_CROSS_NOTICE_M, vehicle.speed * SHUTTLE_CROSS_NOTICE_S) + vehicle.half
        d = 0.0
        while True:
            x, y, fx, fy = self._future(vehicle, d)
            found, along_m, side, tx, ty = self._along_look(x, y)
            if found and along_m > 0 and side < shuttle.half_width + vehicle.half_width + SHUTTLE_SIDE_ROOM_M:
                return True, along_m, side, tx, ty, fx, fy
            if d >= notice:
                _, along_m, side, tx, ty = self._along_look(vehicle.x, vehicle.y)
                return False, along_m, side, tx, ty, vehicle.fx, vehicle.fy
            d = min(d + SHUTTLE_LOOK_STEP_M, notice)

    def _runs_into(self, vehicle: Vehicle, shuttle: Shuttle) -> bool:
        """Whether a vehicle driving on along the roads it plans to take would touch the shuttle's body
        where it stands now, before it has gone RUN_INTO_M."""
        body = (shuttle.x, shuttle.y, shuttle.fx, shuttle.fy, shuttle.half_length, shuttle.half_width)
        d = SHUTTLE_LOOK_STEP_M
        while d <= RUN_INTO_M:
            x, y, fx, fy = self._future(vehicle, d)
            if overlap(*body, x, y, fx, fy, vehicle.half, vehicle.half_width):
                return True
            d += SHUTTLE_LOOK_STEP_M
        return False

    @staticmethod
    def _future(vehicle: Vehicle, ahead_m: float) -> tuple[float, float, float, float]:
        """Where a vehicle's middle will be this much further along the roads it plans to take."""
        road, lane, distance = vehicle.road, vehicle.lane, vehicle.distance + ahead_m
        for nxt in vehicle.ahead:
            if distance <= road.length:
                break
            distance -= road.length
            road, lane = nxt, nxt.lane_from(lane)
        return road.sample(distance, road.lane_offset(lane) + vehicle.aside)

    def _along_look(self, px: float, py: float):
        """Where a point is beside the shuttle's path ahead: how far along it, how far to the side, and
        which way the path runs there."""
        best_side, best_along, best_tx, best_ty = math.inf, 0.0, 0.0, 0.0
        travelled = 0.0
        points = self._look
        for i in range(len(points) - 1):
            ax, ay = points[i]
            dx, dy = points[i + 1][0] - ax, points[i + 1][1] - ay
            length = math.hypot(dx, dy)
            if length <= 0:
                continue
            share = min(1.0, max(0.0, ((px - ax) * dx + (py - ay) * dy) / (length * length)))
            distance = math.hypot(px - ax - dx * share, py - ay - dy * share)
            if distance < best_side:
                best_side, best_along, best_tx, best_ty = distance, travelled + length * share, dx / length, dy / length
            travelled += length
        return best_side < math.inf, best_along, best_side, best_tx, best_ty

    def something_alongside(self, shuttle: Shuttle, shift: float) -> bool:
        """Whether a vehicle beside the shuttle is in the way of it moving across, to the left for a
        positive shift, as a driver looks before changing lane."""
        if abs(shift) < 0.01:
            return False
        fx, fy = shuttle.fx, shuttle.fy
        lx, ly = -fy, fx
        reach = shuttle.half_length * 2 + MIN_GAP_M
        for vehicle in self.vehicles:
            if not vehicle.placed:
                continue
            tx, ty = vehicle.x - shuttle.x, vehicle.y - shuttle.y
            if tx * tx + ty * ty > reach * reach:
                continue
            side = tx * lx + ty * ly
            if side * shift <= 0:
                continue
            across = abs(vehicle.fx * lx + vehicle.fy * ly)
            along = abs(vehicle.fx * fx + vehicle.fy * fy)
            if abs(tx * fx + ty * fy) > shuttle.half_length + along * vehicle.half + across * vehicle.half_width + MIN_GAP_M:
                continue
            if abs(side) < (shuttle.half_width + across * vehicle.half + along * vehicle.half_width
                            + SHUTTLE_SIDE_ROOM_M + SHUTTLE_LANE_MPS):
                return True
        return False

    def place_shuttle(self, shuttle: Shuttle, route: ShuttleRoute | None, along: float, speed: float, dt: float,
                      *, held_by_traffic: bool = False, held_by_light: bool = False, in_traffic: bool = False,
                      point: tuple[float, float] | None = None) -> None:
        """Records where the session moved a shuttle, and how fast. A shuttle that jumped, as onto a new
        route, is put straight in its lane facing its way. Without a route it stands at point."""
        shuttle.speed = speed
        shuttle.in_traffic = in_traffic
        shuttle.wait_s = shuttle.wait_s + dt if held_by_traffic and not held_by_light and speed < STOPPED_MPS else 0.0
        if route is None:
            shuttle.driven = False
            shuttle.gives_way_to.clear()
            shuttle.x, shuttle.y = point
            shuttle.route, shuttle.route_sig, shuttle.along, shuttle.placed = None, "", along, True
            return
        jumped = (not shuttle.placed or shuttle.route_sig != route.sig or along < shuttle.along - 1e-6
                  or along - shuttle.along > max(SHUTTLE_JUMP_M, speed * dt * 2))
        # Driving its route, it drives as the traffic does; posed without one, it cannot give way
        shuttle.driven = True
        if jumped:
            hx, hy = route.direction(along)
            px, py = route.point(along)
            _, shuttle.target_aside = self.network.road_under(px, py, hx, hy, SHUTTLE_ROAD_ALIGN)
            shuttle.aside = shuttle.target_aside
            shuttle.gives_way_to.clear()
            shuttle.wait_s = 0.0
        elif self.something_alongside(shuttle, shuttle.target_aside - shuttle.aside):
            shuttle.target_aside = shuttle.aside
        aside = move_towards(shuttle.aside, shuttle.target_aside, SHUTTLE_LANE_MPS * dt)
        # The look along its side can miss a corner: a move across that would touch a vehicle waits
        if jumped or aside == shuttle.aside or not self._side_blocked(shuttle, route, along, aside):
            shuttle.aside = aside
        shuttle.x, shuttle.y, shuttle.fx, shuttle.fy = route.body(along, shuttle.aside, shuttle.half_length)
        shuttle.route, shuttle.route_sig, shuttle.along, shuttle.placed = route, route.sig, along, True

    def body_blocker(self, shuttle: Shuttle, route: ShuttleRoute, along: float):
        """The first vehicle or other shuttle the shuttle's body would touch, moved this far along its
        route, where they stand now; None when the way is clear. One it already touches is left out:
        moving cannot be blamed for it, and standing still would not part them."""
        x, y, fx, fy = route.body(along, shuttle.aside, shuttle.half_length)
        reach = shuttle.half_length + 10.0
        body = (x, y, fx, fy, shuttle.half_length, shuttle.half_width)
        now = (shuttle.x, shuttle.y, shuttle.fx, shuttle.fy, shuttle.half_length, shuttle.half_width)
        for vehicle in self.vehicles:
            if not vehicle.placed or abs(vehicle.x - x) > reach or abs(vehicle.y - y) > reach:
                continue
            other = (vehicle.x, vehicle.y, vehicle.fx, vehicle.fy, vehicle.half, vehicle.half_width)
            if overlap(*body, *other) and not (shuttle.placed and overlap(*now, *other)):
                return vehicle
        for peer in self.shuttles.values():
            if peer is shuttle or not peer.placed or abs(peer.x - x) > reach + peer.half_length or abs(peer.y - y) > reach + peer.half_length:
                continue
            if self._follows(shuttle, peer.x, peer.y, peer.fx, peer.fy):
                continue
            other = (peer.x, peer.y, peer.fx, peer.fy, peer.half_length, peer.half_width)
            if overlap(*body, *other) and not (shuttle.placed and overlap(*now, *other)):
                return peer
        return None

    @staticmethod
    def _follows(shuttle: Shuttle, x: float, y: float, fx: float, fy: float) -> bool:
        """Whether another shuttle at x, y facing fx, fy is behind this one going its way. It keeps clear,
        as a follower does, so this one may pull away from it even round a bend; otherwise two buses on
        one loop could hold each other up for good at a corner."""
        return (x - shuttle.x) * shuttle.fx + (y - shuttle.y) * shuttle.fy < 0 and fx * shuttle.fx + fy * shuttle.fy > 0.5

    def _side_blocked(self, shuttle: Shuttle, route: ShuttleRoute, along: float, aside: float) -> bool:
        """Whether moving across to aside, where it stands, would put the shuttle's body into a vehicle."""
        x, y, fx, fy = route.body(along, aside, shuttle.half_length)
        body = (x, y, fx, fy, shuttle.half_length, shuttle.half_width)
        now = (shuttle.x, shuttle.y, shuttle.fx, shuttle.fy, shuttle.half_length, shuttle.half_width)
        for vehicle in self.vehicles:
            if not vehicle.placed or abs(vehicle.x - x) > SHUTTLE_BODY_REACH_M or abs(vehicle.y - y) > SHUTTLE_BODY_REACH_M:
                continue
            other = (vehicle.x, vehicle.y, vehicle.fx, vehicle.fy, vehicle.half, vehicle.half_width)
            if overlap(*body, *other) and not overlap(*now, *other):
                return True
        return False

    def clear_shuttles(self) -> None:
        """Sends off the map any vehicle under a shuttle, as one the sample happened to place there."""
        self._own_vehicles()
        self._fill_lanes()
        self._bodies = [(s.x, s.y, s.fx, s.fy, s.half_length, s.half_width) for s in self.shuttles.values() if s.placed]
        for shuttle in self.shuttles.values():
            if not shuttle.placed:
                continue
            for vehicle in self.vehicles:
                # A pinned vehicle is a world event's: it stays where the event happened
                if vehicle.slot in self._pinned:
                    continue
                if vehicle.placed and overlap(shuttle.x, shuttle.y, shuttle.fx, shuttle.fy, shuttle.half_length + MIN_GAP_M,
                                              shuttle.half_width + SHUTTLE_SIDE_ROOM_M, vehicle.x, vehicle.y, vehicle.fx,
                                              vehicle.fy, vehicle.half, vehicle.half_width):
                    self._enter(vehicle)
                    self._pose(vehicle)
        self._bodies = []

    def forget_shuttle(self, shuttle_id: str) -> None:
        self.shuttles.pop(shuttle_id, None)

    # ---------------------------------------------------------------- what is reported

    def frame(self) -> tuple[bytes, int, list[dict]]:
        """The vehicles on the roads for the 3D view, packed as FRAME_RECORD after FRAME_RECORD, and how
        many there are; and the shuttles' bodies."""
        driving = [vehicle for vehicle in self.vehicles if not vehicle.outside]
        packed = bytearray(FRAME_RECORD.size * len(driving))
        pack, size, at = FRAME_RECORD.pack_into, FRAME_RECORD.size, 0
        for vehicle in driving:
            pack(packed, at, vehicle.seed,
                 min(max(round(vehicle.x * 100), -_INT32), _INT32), min(max(round(vehicle.y * 100), -_INT32), _INT32),
                 round(math.degrees(math.atan2(vehicle.fy, vehicle.fx)) * 10), min(round(vehicle.speed * 100), 65535))
            at += size
        shuttles = [{"vehicle_id": s.id, "x_m": round(s.x, 3), "y_m": round(s.y, 3),
                     "heading_deg": round(math.degrees(math.atan2(s.fy, s.fx)), 2), "speed_mps": round(s.speed, 3)}
                    for s in self.shuttles.values() if s.placed]
        return bytes(packed), len(driving), shuttles


    def report(self) -> dict:
        """Counts for tests and diagnostics: vehicles driving and waiting outside, vehicles overlapping the
        one in front in their lane, red lights run, the mean speed, and bodies touching a shuttle."""
        # Filling the lanes numbers the vehicles in them
        self._own_vehicles()
        self._fill_lanes()
        driving = outside = overlaps = 0
        speed = 0.0
        for vehicle in self.vehicles:
            if vehicle.outside:
                outside += 1
                continue
            driving += 1
            speed += vehicle.speed
            queue = self._lane(vehicle.road, vehicle.lane)
            if vehicle.lane_index + 1 < len(queue):
                front = queue[vehicle.lane_index + 1]
                if front.distance - front.half < vehicle.distance + vehicle.half:
                    overlaps += 1
        return {"driving": driving, "outside": outside, "overlaps": overlaps, "red_light_runs": self.red_light_runs,
                "mean_speed_mps": speed / driving if driving else 0.0, "shuttle_overlaps": self.shuttle_overlaps()}

    def shuttle_overlaps(self) -> list[tuple[str, str]]:
        """Each vehicle or other shuttle touching a shuttle, by id."""
        found = []
        shuttles = [s for s in self.shuttles.values() if s.placed]
        for i, shuttle in enumerate(shuttles):
            body = (shuttle.x, shuttle.y, shuttle.fx, shuttle.fy, shuttle.half_length, shuttle.half_width)
            for other in shuttles[i + 1:]:
                if overlap(*body, other.x, other.y, other.fx, other.fy, other.half_length, other.half_width):
                    found.append((shuttle.id, other.id))
            for vehicle in self.vehicles:
                if vehicle.placed and abs(vehicle.x - shuttle.x) < 30 and abs(vehicle.y - shuttle.y) < 30 and overlap(
                        *body, vehicle.x, vehicle.y, vehicle.fx, vehicle.fy, vehicle.half, vehicle.half_width):
                    found.append((shuttle.id, vehicle.id))
        return found


def unpack_frame(packed: bytes) -> list[tuple[int, int, int, int, int]]:
    """A frame's packed vehicles as (seed index, x cm, y cm, heading in tenths of a degree, speed cm/s)."""
    if len(packed) % FRAME_RECORD.size:
        raise ValueError("A frame's vehicles are cut short")
    return list(FRAME_RECORD.iter_unpack(packed))
