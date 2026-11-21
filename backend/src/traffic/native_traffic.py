"""The background traffic of src.traffic.city_traffic, driven natively, and the choice between the two.

backend/native/traffic/traffic_core.cpp is CityTraffic's driving in C++, built into _traffic_native by
Tools/build_native.py. It matches CityTraffic step for step and bit for bit, so either engine continues the
other's checkpoints exactly. It is several times faster, and it lets go of the GIL while a session steps
its traffic and plans and places its shuttles, so the sessions of one process tick on several cores at
once.

NativeCityTraffic has CityTraffic's interface. Its vehicles and shuttles are views of the native state:
reading one reads the traffic as it is now, as CityTraffic's own objects would show.

It walks CityTraffic's pedestrians too (F9_PEDESTRIANS, src.traffic.walk_network), on a native copy of
the area's walk network made once and shared as the road network's is. Its walkers are views of the native
state, as its vehicles are.

engine() picks the class a new or recovered session uses. F9_NATIVE_TRAFFIC sets it:
    auto (default)  native when the module is built from the current source, else Python
    0               always Python
    1               native, or an error saying why it cannot be
"""

from __future__ import annotations

import hashlib
import logging
import os
import threading
from copy import deepcopy
from pathlib import Path

from src.traffic import city_traffic, road_network as road_network_module, signal_cycle, walk_network
from src.traffic.city_traffic import CityTraffic, ShuttlePlan, ShuttleRoute
from src.traffic.road_network import Road, RoadNetwork, class_weight, road_network
from src.traffic.walk_network import WalkNetwork

try:
    from src.traffic import _traffic_native as _native
    _IMPORT_ERROR: str | None = None
except ImportError as error:  # Not built on this machine
    _native = None
    _IMPORT_ERROR = str(error)

LOG = logging.getLogger(__name__)
ENGINE_VARIABLE = "F9_NATIVE_TRAFFIC"
SOURCE = Path(__file__).resolve().parents[2] / "native" / "traffic" / "traffic_core.cpp"

_network_lock = threading.Lock()
_status: tuple[bool, str] | None = None
_reported = False


# ---------------------------------------------------------------- choosing the engine

def _python_constants() -> dict:
    names = ("PLAN_AHEAD_M MAX_PLANNED_ROADS REACTION_S COMFORT_BRAKE HARD_BRAKE YELLOW_BRAKE MIN_GAP_M STOP_MARGIN_M "
             "NODE_STOP_BACK_M PAST_LINE_M CLAIM_MARGIN_M NOTICE_S CLEARED_JUNCTION_M WAIT_PER_RANK_S STANDING_MPS "
             "STOPPED_MPS RELEASE_CLAIM_S STUCK_S ENTER_MPS SHUTTLE_SIGHT_M SHUTTLE_CLEARANCE_M SHUTTLE_BESIDE_M "
             "SHUTTLE_MOVING_MPS SHUTTLE_PASS_BEHIND_M SHUTTLE_PASS_AHEAD_M MAX_ASIDE_M ASIDE_MPS PASSING_MPS "
             "SHUTTLE_JUNCTION_M STANDING_SHUTTLE_JUNCTION_M SHUTTLE_LANE_MPS SHUTTLE_LOOK_M SHUTTLE_LOOK_STEP_M "
             "SHUTTLE_SIDE_ROOM_M SHUTTLE_ONCOMING_ROOM_M SHUTTLE_ROAD_ALIGN SHUTTLE_FACING_SPAN_M SHUTTLE_TAKE_TURN_S "
             "SHUTTLE_GIVE_UP_S SHUTTLE_CROSS_NOTICE_M SHUTTLE_CROSS_NOTICE_S SHUTTLE_JUMP_M RUN_INTO_M "
             "SHUTTLE_BODY_REACH_M SHUTTLE_FOLLOW_EXTRA_M SHUTTLE_SWING_M TOUCH_M NO_LIMIT PIN_PICK_M PIN_TOUCH_M "
             "PINNED_PASS_MPS").split()
    values = {name: getattr(city_traffic, name) for name in names}
    values["LANE_WIDTH_M"] = road_network_module.LANE_WIDTH_M
    for name in ("MOVEMENT_REACH_M", "CONFLICT_M", "CELL_M", "MATCH_M"):
        values[name] = getattr(road_network_module, name)
    for name in ("GREEN_S", "YELLOW_S", "ALL_RED_S", "PHASES"):
        values[name] = getattr(signal_cycle, name)
    # The pedestrians
    names = ("WALKER_ACCEL WALKER_DECEL WALKER_GAP_M WALKER_REACTION_S SUBLANE_M WALKWAY_EDGE_M WALKER_ASIDE_MPS "
             "OVERTAKE_SLOWER_MPS OVERTAKE_NEAR_M OVERTAKE_CLEAR_M WALK_PLAN_M MAX_PLANNED_WALKWAYS PAUSE_CHANCE "
             "PAUSE_MIN_S PAUSE_MAX_S CROSSINGS_DUE_MIN CROSSINGS_DUE_MAX TAKE_CROSSING_CHANCE KERB_WAIT_BACK_M DECIDE_M "
             "CLEAR_OF_ROAD_M SIGNAL_SAFETY_BUFFER_S WALK_CLEAR_S WALKER_LOOK_M ZEBRA_COURTESY_S ZEBRA_RELEASE_S "
             "KERB_GIVE_UP_S CROSSING_ENTERED_M CROSSING_HELD_M SHUTTLE_BODY_STEP_M RAMP_DEPTH_M WALKING WAITING_AT_KERB "
             "ON_CROSSING PAUSED WAITING_FLAG ON_ROAD_FLAG RAMP_FLAG PAUSED_FLAG").split()
    for name in names:
        values[name] = getattr(city_traffic, name)
    values["WALKER_CELL_M"] = city_traffic._CELL_M
    for name in ("WALKER_HALF_M", "SIGNAL", "ZEBRA", "UNCONTROLLED"):
        values[name] = getattr(walk_network, name)
    # Per-lane release, trips and jaywalkers (sidewalks plan phase 5)
    names = ("LANE_RELEASE_MARGIN_M TRIP_REACH_M BUS_WAIT_MIN_S BUS_WAIT_MAX_S GO_IN_CHANCE HOME_STAY_MIN_S HOME_STAY_MAX_S "
             "PLACE_STAY_MIN_S PLACE_STAY_MAX_S AWAY_ON_BUS_MIN_S AWAY_ON_BUS_MAX_S AWAY_RETRY_MIN_S AWAY_RETRY_MAX_S "
             "JAYWALK_KERB_M APPROACH KERB CROSS HESITATE LEAVE RETURN DONE AWAY JAYWALKING").split()
    for name in names:
        values[name] = getattr(city_traffic, name)
    for name in ("HOME", "PLACE", "STOP", "INTERCHANGE", "MIN_CROSSING_SINE", "CUT_REACH_M", "DAY_S"):
        values[name] = getattr(walk_network, name)
    return values


def mismatched_constants() -> list[str]:
    """The tuning constants whose native value differs from the Python module's."""
    if _native is None:
        return []
    native = _native.constants()
    return sorted(name for name, value in _python_constants().items() if native.get(name) != value)


def status() -> tuple[bool, str]:
    """Whether the native engine can drive sessions here, and why not if it cannot."""
    global _status
    if _status is None:
        _status = _check()
    return _status


def _check() -> tuple[bool, str]:
    if _native is None:
        return False, f"the native module is not built ({_IMPORT_ERROR}); run Tools/build_native.py"
    # A checkout carries the source the module must be built from; a bundled build carries only the module
    if SOURCE.is_file():
        current = hashlib.sha256(SOURCE.read_bytes()).hexdigest()
        if _native.SOURCE_HASH != current:
            return False, "the native module was built from an older traffic_core.cpp; run Tools/build_native.py"
    wrong = mismatched_constants()
    if wrong:
        return False, f"the native module's constants differ from city_traffic.py's: {', '.join(wrong)}"
    return True, "built from the current source"


def requested() -> str:
    value = os.environ.get(ENGINE_VARIABLE, "auto").strip().lower()
    if value in ("", "auto"):
        return "auto"
    if value in ("0", "off", "false", "python", "no"):
        return "python"
    if value in ("1", "on", "true", "native", "yes"):
        return "native"
    raise ValueError(f"{ENGINE_VARIABLE} must be auto, 0 or 1, not {value!r}")


def engine() -> type:
    """The traffic class new and recovered sessions use: NativeCityTraffic or CityTraffic."""
    global _reported
    choice = requested()
    usable, reason = status()
    if choice == "native" and not usable:
        raise RuntimeError(f"{ENGINE_VARIABLE}=1 but the native traffic engine cannot run: {reason}")
    chosen = NativeCityTraffic if usable and choice != "python" else CityTraffic
    if not _reported:
        _reported = True
        if chosen is NativeCityTraffic:
            LOG.info("Background traffic: native engine")
        elif choice == "python":
            LOG.info("Background traffic: Python engine (%s=0)", ENGINE_VARIABLE)
        else:
            LOG.warning("Background traffic: Python engine, because %s", reason)
    return chosen


def engine_name() -> str:
    return "native" if engine() is NativeCityTraffic else "python"


# ---------------------------------------------------------------- the network and sample, natively

def supported(network: RoadNetwork) -> bool:
    """Whether the native engine can drive on a network: its junctions must be numbered, as OSM's are."""
    return all(type(road.from_node) is int and type(road.to_node) is int
               and -2**63 <= road.from_node < 2**63 and -2**63 <= road.to_node < 2**63 for road in network.roads)


def native_network(network: RoadNetwork):
    """The native copy of a network, made once and shared by every session on it."""
    built = getattr(network, "_native", None)
    if built is not None:
        return built
    with _network_lock:
        built = getattr(network, "_native", None)
        if built is None:
            built = network._native = _build_network(network)
    return built


def prepare(network: RoadNetwork, walks: WalkNetwork | None = None) -> None:
    """Builds a network's native copy and its sample's starting places ahead of the first session on it, and
    its walk network's and walkers' with pedestrians (at the time of day F9_DAY_START says)."""
    if supported(network):
        native_network(network)
        _seeds(network)
        if walks is not None and walks.network is network:
            native_walks(walks)
            _walker_seeds(walks, walk_network.day_start_s())


def _build_network(network: RoadNetwork):
    junctions: dict[str, int] = {}
    roads = []
    for road in network.roads:
        stops = [(float(at), int(offset), int(phase), junctions.setdefault(str(junction), len(junctions)))
                 for at, offset, phase, junction in road.stops]
        roads.append((road.index, str(road.road_id), road.lanes, float(road.speed), bool(road.oneway), bool(road.service),
                      road.from_node, road.to_node, [float(x) for x in road.xs], [float(y) for y in road.ys],
                      [float(a) for a in road.along], float(road.length), float(road.sx), float(road.sy),
                      float(road.ex), float(road.ey), stops, float(road.rank), float(class_weight(road.cls))))
    exits = [(node, [road.index for road in leaving]) for node, leaving in network.exits.items()]
    grid = [(x, y, [road.index for road in near]) for (x, y), near in network.grid.items()]
    return _native.make_network(network.area, roads, list(junctions), exits, [road.index for road in network.entries],
                                [road.index for road in network.large_entries], grid)


def _seeds(network: RoadNetwork) -> list[tuple]:
    """The sample's vehicles as CityTraffic places them before they drive: worked out once per network."""
    known = getattr(network, "_native_seeds", None)
    if known is None:
        sample = CityTraffic(network)
        for slot, seed in enumerate(network.seeds):
            sample._add(slot, seed)
        known = network._native_seeds = [
            (v.seed, str(v.id), str(v.model), float(v.length), float(v.half), float(v.half_width), bool(v.large),
             float(v.accel), float(v.speed_factor), int(v.rng), v.road.index, int(v.lane), float(v.distance), float(v.speed))
            for v in sample.vehicles]
    return known


def native_walks(walks: WalkNetwork):
    """The native copy of a walk network, made once and shared by every session on it."""
    built = getattr(walks, "_native", None)
    if built is not None:
        return built
    network = native_network(walks.network)
    with _network_lock:
        built = getattr(walks, "_native", None)
        if built is None:
            built = walks._native = _build_walks(walks, network)
    return built


def _build_walks(walks: WalkNetwork, network):
    ways = [(way.index, -1 if way.crossing is None else way.crossing.index, [float(x) for x in way.xs],
             [float(y) for y in way.ys], [float(a) for a in way.along], float(way.length), float(way.width), int(way.mine),
             int(way.from_node), int(way.to_node), int(way.back), float(way.kerb_in), float(way.kerb_out), float(way.sx),
             float(way.sy), float(way.ex), float(way.ey), int(way.axis), float(way.cost)) for way in walks.ways]
    crossings = [(c.index, int(c.control), str(c.junction), int(c.offset), int(c.phase), float(c.cx), float(c.cy),
                  float(c.dx), float(c.dy), float(c.half_length), float(c.half_width),
                  [(int(road), float(at), float(band), float(slant), *map(float, c.lanes_at.get(road, (0.0, 0.0))))
                   for road, at, band, slant in c.roads])
                 for c in walks.crossings]
    exits = [[way.index for way in leaving] for leaving in walks.exits]
    by_road = [[(float(at), c.index, float(band), float(slant)) for at, c, band, slant in entries]
               for entries in walks.by_road]
    nodes = [(float(x), float(y)) for x, y in walks.nodes]
    attractors = [(int(a.kind), int(a.node), float(a.weight)) for a in walks.attractors]
    return _native.make_walk_network(network, walks.source_hash, ways, crossings, exits, by_road, nodes, attractors,
                                     [float(share) for share in walks.hourly])


def _walker_seeds(walks: WalkNetwork, day_start_s: float) -> list[tuple]:
    """The walkers as CityTraffic places them before they walk, at a time of day: worked out once per walk
    network and time."""
    cache = getattr(walks, "_native_walkers", None)
    if cache is None:
        cache = walks._native_walkers = {}
    known = cache.get(day_start_s)
    if known is None:
        sample = CityTraffic(walks.network, walks, day_start_s)
        for seed in walks.seeds:
            sample._add_walker(seed)
        known = cache[day_start_s] = [
            (int(w.seed), str(w.id), w.way.index, float(w.distance), int(w.sublane), float(w.aside), float(w.speed),
             float(w.target), float(w.desired), int(w.state), float(w.wait_s), float(w.kerb_wait_s), int(w.rng),
             int(w.crossings_due), int(w.granted), [way.index for way in w.ahead], int(w.dest), int(w.home))
            for w in sample.walkers]
    return known


def jaywalk_tuple(jaywalk: city_traffic.Jaywalk) -> tuple:
    """A jaywalk as the native engine takes it (start_jaywalk)."""
    return (jaywalk.key, jaywalk.slot, jaywalk.until, jaywalk.phase, jaywalk.s, jaywalk.wait_s, jaywalk.pause_s,
            jaywalk.road, jaywalk.distance, jaywalk.side, jaywalk.ax, jaywalk.ay, jaywalk.bx, jaywalk.by, jaywalk.cx,
            jaywalk.cy, jaywalk.dx, jaywalk.dy, jaywalk.half_length, jaywalk.half_width,
            [(int(road), float(at), float(band), float(slant)) for road, at, band, slant in jaywalk.roads],
            jaywalk.from_x, jaywalk.from_y, jaywalk.to_x, jaywalk.to_y, jaywalk.rejoin_way, jaywalk.rejoin_distance,
            jaywalk.back_way, jaywalk.back_distance)


def native_route(route: ShuttleRoute, walks: WalkNetwork | None = None):
    """A shuttle route's geometry, natively, made the first time the native engine drives on it, with the
    crossings it passes over on a walk network."""
    known = route.native
    if known is None or known[0] is not walks:
        crossings = [] if walks is None else [(float(at), crossing.index, float(band))
                                              for at, crossing, band in walks.route_crossings(route)]
        made = _native.make_route(route.points, route.along, float(route.length), bool(route.closed),
                                  [(float(a), float(d)) for a, d in route.bends], route.sig, crossings)
        known = route.native = (walks, made)
    return known[1]


# ---------------------------------------------------------------- views of the native state

class NativeClaim:
    """A vehicle's claim on a junction, as city_traffic.Claim shows it."""

    __slots__ = ("node", "from_r", "from_lane", "to_r", "to_lane", "entered")

    def __init__(self, node, from_r, from_lane, to_r, to_lane, entered):
        self.node, self.from_r, self.from_lane, self.to_r, self.to_lane, self.entered = \
            node, from_r, from_lane, to_r, to_lane, entered

    @property
    def movement(self):
        return self.from_r.index, self.from_lane, self.to_r.index, self.to_lane


class NativeVehicle:
    """One vehicle of the native traffic, with city_traffic.Vehicle's attributes, read as it is now."""

    __slots__ = ("_traffic", "slot")

    def __init__(self, traffic: NativeCityTraffic, slot: int):
        self._traffic, self.slot = traffic, slot

    def __getattr__(self, name):
        if name.startswith("__"):
            raise AttributeError(name)
        roads = self._traffic.network.roads
        value = self._traffic._core.vehicle_field(self.slot, name)
        if name == "road":
            return roads[value]
        if name == "ahead":
            return [roads[index] for index in value]
        if name == "claims":
            return [NativeClaim(node, roads[from_r], from_lane, roads[to_r], to_lane, entered)
                    for node, from_r, from_lane, to_r, to_lane, entered in value]
        return value

    def has_claim(self, to: Road) -> bool:
        return any(claim.to_r is to and not claim.entered for claim in self.claims)

    def has_claim_from(self, road: Road) -> bool:
        return any(claim.from_r is road and not claim.entered for claim in self.claims)

    def __repr__(self) -> str:
        return f"NativeVehicle({self.slot}, {self.id!r})"


class NativeShuttle:
    """A shuttle as the native traffic sees it, with city_traffic.Shuttle's attributes, read and set live."""

    __slots__ = ("_traffic", "id")

    def __init__(self, traffic: NativeCityTraffic, shuttle_id: str):
        object.__setattr__(self, "_traffic", traffic)
        object.__setattr__(self, "id", shuttle_id)

    def __getattr__(self, name):
        if name.startswith("__"):
            raise AttributeError(name)
        if name == "route":
            return self._traffic._routes.get(self.id)
        if name == "ahead_cache":
            return None
        fields = self._traffic._core.shuttle_get(self.id)
        if name not in fields:
            raise AttributeError(name)
        value = fields[name]
        return set(value) if name == "gives_way_to" else value

    def __setattr__(self, name, value):
        if name == "route":
            self._traffic._core.shuttle_route_set(self.id, None if value is None else self._traffic._route(value))
            if value is None:
                self._traffic._routes.pop(self.id, None)
            else:
                self._traffic._routes[self.id] = value
            return
        if name == "id":
            raise AttributeError("a shuttle's id does not change")
        self._traffic._core.shuttle_set(self.id, name, value)

    def __repr__(self) -> str:
        return f"NativeShuttle({self.id!r})"


class NativeWalker:
    """One walker of the native traffic, with city_traffic.Walker's attributes, read as it is now."""

    __slots__ = ("_traffic", "slot")

    def __init__(self, traffic: NativeCityTraffic, slot: int):
        self._traffic, self.slot = traffic, slot

    def __getattr__(self, name):
        if name.startswith("__"):
            raise AttributeError(name)
        fields = self._traffic._core.walker(self.slot)
        if name not in fields:
            raise AttributeError(name)
        value = fields[name]
        ways = self._traffic.walks.ways
        if name == "way":
            return ways[value]
        if name == "ahead":
            return [ways[index] for index in value]
        return value

    def __repr__(self) -> str:
        return f"NativeWalker({self.slot}, {self.id!r})"


# ---------------------------------------------------------------- the traffic

class NativeCityTraffic:
    """One session's background traffic, driven natively: city_traffic.CityTraffic's interface and driving."""

    ENGINE = "native"

    def __init__(self, network: RoadNetwork, core, walks: WalkNetwork | None = None):
        self.network = network
        self._core = core
        # The walk network its pedestrians walk, or None without pedestrians
        self.walks = walks
        self._walker_views: list[NativeWalker] | None = None
        # As CityTraffic's: route key -> (the waypoints it was made from, its geometry)
        self._shuttle_routes: dict[str, tuple] = {}
        # Each shuttle's route, as given, so a shuttle reads back the object it was placed on
        self._routes: dict[str, ShuttleRoute] = {}
        self._vehicle_views: list[NativeVehicle] | None = None
        self._shuttle_views: dict[str, NativeShuttle] = {}

    # ------------------------------------------------ building and copying

    @classmethod
    def start(cls, area: str, pedestrians: bool | None = None, day_start_s: float | None = None):
        """As CityTraffic.start: with pedestrians (by default as F9_PEDESTRIANS says), they walk too, as many
        out as the time of day its clock starts at (by default as F9_DAY_START says) has."""
        network = road_network(area)
        if network is None:
            return None
        if pedestrians is None:
            pedestrians = walk_network.enabled()
        return cls.on(network, walk_network.walk_network(area) if pedestrians else None, day_start_s)

    @classmethod
    def on(cls, network: RoadNetwork, walks: WalkNetwork | None = None, day_start_s: float | None = None):
        if not supported(network):
            LOG.warning("Network %s has junctions the native traffic engine cannot number; driving it in Python",
                        network.area)
            return CityTraffic.on(network, walks, day_start_s)
        if walks is not None and walks.network is not network:
            LOG.warning("Walk network %s is not on the road network it is driven with; driving it in Python", walks.area)
            return CityTraffic.on(network, walks, day_start_s)
        day_start = float(walk_network.day_start_s() if day_start_s is None else day_start_s)
        core = _native.Traffic(native_network(network), None if walks is None else native_walks(walks))
        core.day_start_s = day_start
        for seed in _seeds(network):
            core.add_vehicle(*seed)
        core.pose_all()
        if walks is not None:
            for seed in _walker_seeds(walks, day_start):
                core.add_walker(*seed)
        return cls(network, core, walks)

    @classmethod
    def from_state(cls, state: dict, network: RoadNetwork | None = None, walks: WalkNetwork | None = None):
        """Traffic as state() left it (either engine's), checked as CityTraffic.from_state checks it: its
        walkers too, if it walks any, from where their seeds place them for a checkpoint without them."""
        day_start = state.get("day_start_s")
        traffic = cls.on(network, walks, day_start) if network is not None else \
            cls.start(state["area"], pedestrians=True if "walkers" in state else None, day_start_s=day_start)
        if traffic is not None and not isinstance(traffic, NativeCityTraffic):
            return CityTraffic.from_state(state, traffic.network, traffic.walks)
        if traffic is not None and traffic._core.walker_count() and "walkers" in state:
            traffic._load_walkers(state)
        if traffic is None or len(state["vehicles"]) != len(traffic.vehicles):
            raise ValueError("Traffic checkpoint does not match the area's sample")
        roads = traffic.network.roads
        vehicles = []
        for item in state["vehicles"]:
            road, lane, distance, speed, stopped_s, wait_s, aside, outside, rng, ahead, claims = item
            vehicles.append((roads[road].index, lane, float(distance), float(speed), float(stopped_s), float(wait_s),
                             float(aside), bool(outside), int(rng), [roads[index].index for index in ahead],
                             [(node, roads[from_r].index, from_lane, roads[to_r].index, to_lane, bool(entered))
                              for node, from_r, from_lane, to_r, to_lane, entered in claims]))
        core = traffic._core
        core.load(float(state["time_s"]), int(state["red_light_runs"]), vehicles)
        for item in state["shuttles"]:
            (shuttle_id, x, y, fx, fy, half_length, half_width, speed, driven, gives_way_to, wait_s, stops_within,
             in_traffic, aside, target_aside, placed, along, route_sig) = item
            # As a fresh record in the shuttle's place, as CityTraffic.from_state makes one
            core.shuttle(shuttle_id, float(half_length), float(half_width))
            core.shuttle_route_set(shuttle_id, None)
            for name, value in (("half_length", float(half_length)), ("half_width", float(half_width)), ("x", float(x)), ("y", float(y)), ("fx", float(fx)), ("fy", float(fy)),
                                ("speed", float(speed)), ("driven", bool(driven)), ("wait_s", float(wait_s)),
                                ("gives_way_to", set(gives_way_to)), ("stops_within_m", float(stops_within)),
                                ("in_traffic", bool(in_traffic)), ("aside", float(aside)),
                                ("target_aside", float(target_aside)), ("placed", bool(placed)), ("along", float(along)),
                                ("route_sig", route_sig)):
                core.shuttle_set(shuttle_id, name, value)
        core.set_hazards([(int(road), [tuple(span) for span in spans]) for road, spans in state["hazards"]], False)
        # A checkpoint from before world events were played out in the traffic has neither
        count = len(traffic.vehicles)
        pins = []
        for key, slots, until in state.get("pins", ()):
            if not isinstance(key, str) or not all(type(slot) is int and 0 <= slot < count for slot in slots):
                raise ValueError("Traffic checkpoint pins a vehicle it does not have")
            pins.append((key, list(slots), float(until)))
        faults = [(str(junction), float(until)) for junction, until in state.get("signal_faults", ())]
        core.load_events(pins, faults)
        return traffic

    def _load_walkers(self, state: dict) -> None:
        items = state["walkers"]
        if state.get("walk_source") != self.walks.source_hash or len(items) != self._core.walker_count():
            raise ValueError("Traffic checkpoint's walkers do not match the area's walk network")
        count = len(self.walks.ways)
        walkers = []
        for (way, distance, sublane, aside, speed, walker_state, wait_s, kerb_wait_s, rng, due, granted, ahead, dest,
             home) in city_traffic.walker_items(items, len(self.walks.attractors)):
            if not all(type(index) is int and 0 <= index < count for index in (way, *ahead)):
                raise IndexError("Traffic checkpoint names a walkway the walk network does not have")
            walkers.append((way, float(distance), int(sublane), float(aside), float(speed), int(walker_state), float(wait_s),
                            float(kerb_wait_s), int(rng), int(due), int(granted), list(ahead), dest, home))
        occupied, claimed, waits, releases = state["crossings"]
        crossings = len(self.walks.crossings)
        if not all(type(index) is int and 0 <= index < crossings
                   for index in [*occupied, *claimed, *(i for i, _ in waits), *(i for i, _ in releases)]):
            raise ValueError("Traffic checkpoint names a crossing the walk network does not have")
        self._core.load_walkers(walkers, list(occupied), list(claimed), [(i, float(s)) for i, s in waits],
                                [(i, float(until)) for i, until in releases])
        self._walker_views = None
        for item in state.get("jaywalks", ()):
            self._core.start_jaywalk(jaywalk_tuple(city_traffic.load_jaywalk(self, item)), False)

    def __deepcopy__(self, memo) -> NativeCityTraffic:
        """A staged session copies its actor every tick: the copy shares the vehicles until either steps
        (as CityTraffic's copy does), so taking one costs next to nothing."""
        copy = NativeCityTraffic.__new__(NativeCityTraffic)
        copy.network = self.network
        copy._core = self._core.copy()
        # Keyed by the actor's own waypoint lists, which the same deepcopy copies: keep them the same objects
        copy._shuttle_routes = {key: (deepcopy(waypoints, memo), route)
                                for key, (waypoints, route) in self._shuttle_routes.items()}
        copy._routes = dict(self._routes)
        copy._vehicle_views = None
        copy._shuttle_views = {}
        copy.walks = self.walks
        copy._walker_views = None
        memo[id(self)] = copy
        return copy

    def state(self) -> dict:
        return self._core.state()

    # ------------------------------------------------ what CityTraffic exposes as attributes

    @property
    def time_s(self) -> float:
        return self._core.time_s

    @time_s.setter
    def time_s(self, value: float) -> None:
        self._core.time_s = float(value)

    @property
    def red_light_runs(self) -> int:
        return self._core.red_light_runs

    @property
    def day_start_s(self) -> float:
        return self._core.day_start_s

    @property
    def jaywalks(self) -> dict[str, dict]:
        """The jaywalks under way, by their events' keys: each walker's slot, what it is doing (a
        city_traffic jaywalk phase), how far along its leg and when its event ends."""
        return {key: {"slot": slot, "phase": phase, "s": s, "until": until}
                for key, slot, phase, s, until in self._core.jaywalks()}

    @property
    def vehicles(self) -> list[NativeVehicle]:
        if self._vehicle_views is None:
            self._vehicle_views = [NativeVehicle(self, slot) for slot in range(self._core.vehicle_count())]
        return self._vehicle_views

    @property
    def walkers(self) -> list[NativeWalker]:
        if self._walker_views is None:
            self._walker_views = [NativeWalker(self, slot) for slot in range(self._core.walker_count())]
        return self._walker_views

    @property
    def _occupied(self) -> set[int]:
        return set(self._core.crossing_sets()[0])

    @property
    def _claimed(self) -> set[int]:
        return set(self._core.crossing_sets()[1])

    @property
    def crossing_waits(self) -> dict[int, float]:
        return dict(self._core.crossing_sets()[2])

    @property
    def crossing_releases(self) -> dict[int, float]:
        return dict(self._core.crossing_sets()[3])

    def _fault(self, junction: str) -> float | None:
        """As CityTraffic._fault: when a junction's fault clears, while its lights are stuck; else None."""
        until = self.signal_faults.get(junction)
        return until if until is not None and self.time_s < until else None

    @property
    def shuttles(self) -> dict[str, NativeShuttle]:
        return {shuttle_id: self._shuttle_view(shuttle_id) for shuttle_id in self._core.shuttle_ids()}

    @property
    def hazards(self) -> dict[int, list[tuple[float, float]]]:
        return {road: [tuple(span) for span in spans] for road, spans in self._core.hazards()}

    @property
    def pins(self) -> dict[str, tuple[tuple[int, ...], float]]:
        return {key: (tuple(slots), until) for key, slots, until in self._core.pins()}

    @property
    def signal_faults(self) -> dict[str, float]:
        return dict(self._core.signal_faults())

    @property
    def _pinned(self) -> set[int]:
        return set(self._core.pinned())

    def _shuttle_view(self, shuttle_id: str) -> NativeShuttle:
        view = self._shuttle_views.get(shuttle_id)
        if view is None:
            view = self._shuttle_views[shuttle_id] = NativeShuttle(self, shuttle_id)
        return view

    # ------------------------------------------------ hazards and world events

    def set_hazards(self, hazards: list[tuple[list[tuple[float, float]], bool]]) -> None:
        self._core.set_hazards(list(city_traffic.hazard_spans(self.network, hazards).items()), True)

    locate = CityTraffic.locate

    def pin_vehicle(self, key: str, road: Road, distance: float, lane: int, until_s: float, count: int = 1) -> bool:
        return self._core.pin_vehicle(key, road.index, float(distance), int(lane), float(until_s), int(count))

    def pin_until(self, key: str, until_s: float) -> None:
        self._core.pin_until(key, float(until_s))

    def set_signal_fault(self, junction_id: str, until_s: float) -> None:
        self._core.set_signal_fault(junction_id, float(until_s))

    def jaywalk(self, key: str, road: Road, distance: float, until_s: float) -> bool:
        """As CityTraffic.jaywalk: the jaywalk is worked out here (city_traffic.plan_jaywalk), walked natively."""
        if any(known == key for known, *_ in self._core.jaywalks()):
            return True
        if not self._core.walker_count() or until_s <= self.time_s:
            return False
        jaywalk = city_traffic.plan_jaywalk(self, key, road, distance, until_s)
        if jaywalk is None:
            return False
        self._core.start_jaywalk(jaywalk_tuple(jaywalk), True)
        return True

    # ------------------------------------------------ driving

    def step(self, dt: float, time_s: float) -> None:
        self._core.step(float(dt), float(time_s))

    # ------------------------------------------------ shuttles

    def shuttle_route(self, key: str, waypoints) -> ShuttleRoute:
        known = self._shuttle_routes.get(key)
        if known is None or known[0] is not waypoints:
            known = self._shuttle_routes[key] = (waypoints, ShuttleRoute([(p.x, p.y) for p in waypoints]))
        return known[1]

    def shuttle(self, shuttle_id: str, half_length: float, half_width: float) -> NativeShuttle:
        self._core.shuttle(shuttle_id, float(half_length), float(half_width))
        return self._shuttle_view(shuttle_id)

    def _route(self, route: ShuttleRoute):
        return native_route(route, self.walks)

    def plan_shuttle(self, shuttle, route: ShuttleRoute, along: float, cruise: float) -> ShuttlePlan:
        limit, cap, held, bend, pedestrians = self._core.plan_shuttle(shuttle.id, self._route(route), float(along),
                                                                      float(cruise))
        plan = ShuttlePlan()
        plan.limit_mps, plan.cap_m, plan.held, plan.bend_mps, plan.pedestrian_mps = limit, cap, held, bend, pedestrians
        return plan

    def place_shuttle(self, shuttle, route: ShuttleRoute | None, along: float, speed: float, dt: float,
                      *, held_by_traffic: bool = False, held_by_light: bool = False, in_traffic: bool = False,
                      point: tuple[float, float] | None = None) -> None:
        px, py = (0.0, 0.0) if route is not None else point
        self._core.place_shuttle(shuttle.id, None if route is None else self._route(route), float(along), float(speed),
                                 float(dt), bool(held_by_traffic), bool(held_by_light), bool(in_traffic), float(px), float(py))
        if route is None:
            self._routes.pop(shuttle.id, None)
        else:
            self._routes[shuttle.id] = route

    def body_blocker(self, shuttle, route: ShuttleRoute, along: float):
        kind, index, peer = self._core.body_blocker(shuttle.id, self._route(route), float(along))
        if kind == 1:
            return self.vehicles[index]
        if kind == 2:
            return self._shuttle_view(peer)
        return None

    def something_alongside(self, shuttle, shift: float) -> bool:
        return self._core.something_alongside(shuttle.id, float(shift))

    def clear_shuttles(self) -> None:
        self._core.clear_shuttles()

    def forget_shuttle(self, shuttle_id: str) -> None:
        self._core.forget_shuttle(shuttle_id)
        self._routes.pop(shuttle_id, None)
        self._shuttle_views.pop(shuttle_id, None)

    # ------------------------------------------------ what is reported

    def frame(self) -> tuple[bytes, int, list[dict]]:
        packed, count = self._core.frame()
        shuttles = []
        for shuttle_id in self._core.shuttle_ids():
            s = self._core.shuttle_get(shuttle_id)
            if s["placed"]:
                shuttles.append({"vehicle_id": shuttle_id, "x_m": round(s["x"], 3), "y_m": round(s["y"], 3),
                                 "heading_deg": round(city_traffic.math.degrees(city_traffic.math.atan2(s["fy"], s["fx"])), 2),
                                 "speed_mps": round(s["speed"], 3)})
        return packed, count, shuttles

    def pedestrian_frame(self) -> tuple[bytes, int]:
        """The walkers out (not away) for the 3D view, packed as city_traffic.PED_RECORD after PED_RECORD,
        and how many."""
        if not self._core.walker_count():
            return b"", 0
        packed = self._core.pedestrian_frame()
        return packed, len(packed) // city_traffic.PED_RECORD.size

    def report(self) -> dict:
        driving, outside, overlaps, red_light_runs, mean_speed = self._core.report()
        report = {"driving": driving, "outside": outside, "overlaps": overlaps, "red_light_runs": red_light_runs,
                  "mean_speed_mps": mean_speed, "shuttle_overlaps": self.shuttle_overlaps()}
        if self._core.walker_count():
            walking, waiting, crossing, mean_walk, away, jaywalking, trips = self._core.walker_report()
            report.update(walkers_walking=walking, walkers_waiting=waiting, walkers_crossing=crossing,
                          walker_mean_speed_mps=mean_walk, walkers_away=away, walkers_jaywalking=jaywalking,
                          walkers_on_trips=trips)
            report["pedestrian_hits"], report["footpath_overlaps"] = self._walker_contacts()
        return report

    def pedestrian_hits(self) -> list[tuple[str, str]]:
        """As CityTraffic.pedestrian_hits: each vehicle or shuttle touching a walker on a crossing's road part."""
        return self._walker_contacts()[0]

    def footpath_overlaps(self) -> list[tuple[str, str]]:
        """As CityTraffic.footpath_overlaps."""
        return self._walker_contacts()[1]

    def _walker_contacts(self) -> tuple[list[tuple[str, str]], list[tuple[str, str]]]:
        found, overhangs = self._core.walker_contacts()
        return [tuple(pair) for pair in found], [tuple(pair) for pair in overhangs]

    def shuttle_overlaps(self) -> list[tuple[str, str]]:
        return [tuple(pair) for pair in self._core.shuttle_overlaps()]
