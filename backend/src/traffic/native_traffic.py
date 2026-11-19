"""The background traffic of src.traffic.city_traffic, driven natively, and the choice between the two.

backend/native/traffic/traffic_core.cpp is CityTraffic's driving in C++, built into _traffic_native by
Tools/build_native.py. It matches CityTraffic step for step and bit for bit, so either engine continues the
other's checkpoints exactly. It is several times faster, and it lets go of the GIL while a session steps
its traffic and plans and places its shuttles, so the sessions of one process tick on several cores at
once.

NativeCityTraffic has CityTraffic's interface. Its vehicles and shuttles are views of the native state:
reading one reads the traffic as it is now, as CityTraffic's own objects would show.

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

from src.traffic import city_traffic, road_network as road_network_module, signal_cycle
from src.traffic.city_traffic import CityTraffic, ShuttlePlan, ShuttleRoute
from src.traffic.road_network import Road, RoadNetwork, class_weight, road_network

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


def prepare(network: RoadNetwork) -> None:
    """Builds a network's native copy and its sample's starting places ahead of the first session on it."""
    if supported(network):
        native_network(network)
        _seeds(network)


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


def native_route(route: ShuttleRoute):
    """A shuttle route's geometry, natively, made the first time the native engine drives on it."""
    made = route.native
    if made is None:
        made = route.native = _native.make_route(route.points, route.along, float(route.length), bool(route.closed),
                                                 [(float(a), float(d)) for a, d in route.bends], route.sig)
    return made


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
            self._traffic._core.shuttle_route_set(self.id, None if value is None else native_route(value))
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


# ---------------------------------------------------------------- the traffic

class NativeCityTraffic:
    """One session's background traffic, driven natively: city_traffic.CityTraffic's interface and driving."""

    ENGINE = "native"

    def __init__(self, network: RoadNetwork, core):
        self.network = network
        self._core = core
        # As CityTraffic's: route key -> (the waypoints it was made from, its geometry)
        self._shuttle_routes: dict[str, tuple] = {}
        # Each shuttle's route, as given, so a shuttle reads back the object it was placed on
        self._routes: dict[str, ShuttleRoute] = {}
        self._vehicle_views: list[NativeVehicle] | None = None
        self._shuttle_views: dict[str, NativeShuttle] = {}

    # ------------------------------------------------ building and copying

    @classmethod
    def start(cls, area: str):
        network = road_network(area)
        return None if network is None else cls.on(network)

    @classmethod
    def on(cls, network: RoadNetwork):
        if not supported(network):
            LOG.warning("Network %s has junctions the native traffic engine cannot number; driving it in Python",
                        network.area)
            return CityTraffic.on(network)
        core = _native.Traffic(native_network(network))
        for seed in _seeds(network):
            core.add_vehicle(*seed)
        core.pose_all()
        return cls(network, core)

    @classmethod
    def from_state(cls, state: dict, network: RoadNetwork | None = None):
        """Traffic as state() left it (either engine's), checked as CityTraffic.from_state checks it."""
        traffic = cls.on(network) if network is not None else cls.start(state["area"])
        if traffic is not None and not isinstance(traffic, NativeCityTraffic):
            return CityTraffic.from_state(state, traffic.network)
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
    def vehicles(self) -> list[NativeVehicle]:
        if self._vehicle_views is None:
            self._vehicle_views = [NativeVehicle(self, slot) for slot in range(self._core.vehicle_count())]
        return self._vehicle_views

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

    def plan_shuttle(self, shuttle, route: ShuttleRoute, along: float, cruise: float) -> ShuttlePlan:
        limit, cap, held, bend = self._core.plan_shuttle(shuttle.id, native_route(route), float(along), float(cruise))
        plan = ShuttlePlan()
        plan.limit_mps, plan.cap_m, plan.held, plan.bend_mps = limit, cap, held, bend
        return plan

    def place_shuttle(self, shuttle, route: ShuttleRoute | None, along: float, speed: float, dt: float,
                      *, held_by_traffic: bool = False, held_by_light: bool = False, in_traffic: bool = False,
                      point: tuple[float, float] | None = None) -> None:
        px, py = (0.0, 0.0) if route is not None else point
        self._core.place_shuttle(shuttle.id, None if route is None else native_route(route), float(along), float(speed),
                                 float(dt), bool(held_by_traffic), bool(held_by_light), bool(in_traffic), float(px), float(py))
        if route is None:
            self._routes.pop(shuttle.id, None)
        else:
            self._routes[shuttle.id] = route

    def body_blocker(self, shuttle, route: ShuttleRoute, along: float):
        kind, index, peer = self._core.body_blocker(shuttle.id, native_route(route), float(along))
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

    def report(self) -> dict:
        driving, outside, overlaps, red_light_runs, mean_speed = self._core.report()
        return {"driving": driving, "outside": outside, "overlaps": overlaps, "red_light_runs": red_light_runs,
                "mean_speed_mps": mean_speed, "shuttle_overlaps": self.shuttle_overlaps()}

    def shuttle_overlaps(self) -> list[tuple[str, str]]:
        return [tuple(pair) for pair in self._core.shuttle_overlaps()]
