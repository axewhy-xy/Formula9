"""
Authoritative planar fleet and domain state for the BE-M2-06 integrated slice.

Author: Nur Syazliyarni Binte Zazali
Date Created: 28/10/2026
Last Modified: 28/10/2026

Description:
    Reuses BE-M2-02 separation/motion and BE-M2-04/05 validation, incident and
    energy services with one authored five-shuttle fixture. Geometry and domain
    services load before ticking. Private progress, holds, depot paths and charge
    timers are checkpointed with committed public state by the session service.
    This controller owns simulation consequences; clients only display them.
"""

from __future__ import annotations

import base64
from copy import deepcopy
from dataclasses import asdict
from functools import lru_cache
import hashlib
import json
import logging
import math
from pathlib import Path
from uuid import uuid4

import networkx as nx

from contracts.v2.models import Charger, Depot, Disruption, Event, FullSnapshot, Position, RouteStop
from src.energy.assessment import RUBRIC, RUBRIC_REVISION
from src.energy.bookings import finish_hold, live_bookings, record_hold, settle
from src.energy.decisions import with_battery_decisions
from src.energy.forecast import reserve_kwh
from src.energy.model import ENERGY_R1, auxiliary_energy_kwh, charge_battery_kwh, drive_energy_kwh, seconds_to_full, use_energy
from src.persistence.training_store import TrainingStoreError
from src.routing.road_graph import RoadGraphSnapshot
from src.schemas.shuttle_data import ShuttleState, Vector2D
from src.services.asset_loader import MAX_ROUTE_WAYPOINTS, validate_routes
from src.services.incidents import FIXTURE_PATH as INCIDENT_FIXTURE, IncidentService
from src.services.incident_scenarios.simulated_catalog import SimulatedIncidentCatalog
from src.services.route_edit import RouteEditService
from src.services.session_actor import SessionActor
from src.traffic import incident_hazards, route_signals, signal_cycle
from src.traffic.city_traffic import COMFORT_BRAKE, HARD_BRAKE, PLAN_AHEAD_M, SHUTTLE_ACCEL, CityTraffic, move_towards

logger = logging.getLogger(__name__)

FIXTURE_PATH = Path(__file__).resolve().parents[2] / "fixtures/m2_integrated.json"
SCENARIO_SELECTOR = "scenario-m2-integrated-r1"
SCENARIO_ID = "m2-integrated"
EPSILON = 1e-8
FULL_TOLERANCE_KWH = 0.01  # auxiliary use keeps a plugged-in battery just under capacity
YARD_WAIT_M = 15.0  # a shuttle waits this far short of the yard, clear of one driving out
# The bus model the 3D view draws reaches this far ahead of a shuttle's position, so it stops this far
# short of a red light's stop line; the traffic keeps clear of a body this long either way and this wide
SHUTTLE_FRONT_M = 6.6
SHUTTLE_HALF_WIDTH_M = 2.3
SIGNAL_AREA = "toa-payoh"
# The incidents' hazards are worked out again this often, as the 3D view did, so a queue grows smoothly
HAZARD_REFRESH_TICKS = 10
# The background traffic steps every this many ticks: a tenth of a second, the longest step the 3D view
# ever took, and on the ticks a snapshot is published, so each traffic frame shows a fresh step
TRAFFIC_STEP_TICKS = 2
# What every incident says the session models of the roads
TRAFFIC_ASSUMPTION = ("Background traffic is a 0.1% sample of Singapore's registered vehicles driving a simple "
                      "car-following model on OpenStreetMap roads; shuttles keep to the kerb lane, queue behind it and "
                      "stop at simulated traffic lights with made-up timings.")
# The longest a light can hold a shuttle: a whole yellow and red, arriving just as its green ends
SIGNAL_WAIT_S = signal_cycle.PHASE_S * (signal_cycle.PHASES - 1) + signal_cycle.YELLOW_S + signal_cycle.ALL_RED_S


def geometry(identifier: str, points: list) -> dict:
    """Validate one bounded path and derive its actual metre length."""
    return validate_routes([{"route_ID": identifier, "waypoints": [{"x": p[0], "y": p[1]} for p in points]}], MAX_ROUTE_WAYPOINTS)[0]


class WorkflowDefinition:
    """Shared authored geometry and thread-safe domain services, loaded off-tick."""

    def __init__(self):
        self.data = json.loads(FIXTURE_PATH.read_bytes())
        self.network = json.loads((FIXTURE_PATH.parent / self.data["route_edit"]["network_fixture"]).read_bytes())
        incident = json.loads(INCIDENT_FIXTURE.read_bytes())
        incident.update(self.data["incident_overrides"])
        incident["assumptions"] = [*incident["assumptions"], *self.data["assumptions"]]
        # The controller's revision changes with how it drives: r2 drives the shuttles through the traffic
        self.signature = hashlib.sha256(json.dumps({"fixture": self.data, "network": self.network, "incident": incident,
            "energy": asdict(ENERGY_R1), "controller": "integrated-planar-r2"}, sort_keys=True).encode()).hexdigest()
        graph = nx.MultiDiGraph()
        for identifier, point in self.network["nodes"].items():
            graph.add_node(int(identifier), x=float(point[0]), y=float(point[1]), z=0.0)
        for identifier, edge in self.network["edges"].items():
            parts = identifier.split(":")
            graph.add_edge(edge["source"], edge["target"], key=(parts[0], int(parts[1]), parts[2]),
                length_m=sum(math.dist(a, b) for a, b in zip(edge["path"], edge["path"][1:])),
                speed_estimate_kph=self.data["speed_mps"] * 3.6)
        self.graph = RoadGraphSnapshot(nx.freeze(graph), "directed", ())
        self.routes = RouteEditService(self, fixture={**self.data["route_edit"], "network": self.network,
                                                      "operating_node_ids": sorted(int(n) for n in self.network["nodes"])})
        self.incidents = IncidentService(self.routes, fixture=incident)
        original = self.compile_route(self.data["route_edit"]["original_directed_edge_ids"])[0]
        self.actor_routes = [original, *self.data["peer_patterns"]]
        # Every depot and its charger bays; each charger belongs to exactly one depot.
        self.depots = self.data["depots"]
        self.bay_positions = {charger_id: bay for depot in self.depots for charger_id, bay in depot["bays"].items()}
        self.charger_depot = {charger_id: depot for depot in self.depots for charger_id in depot["bays"]}
        # Each depot has its own yard. Separate exit aisles avoid opposing bay departures meeting
        # head-on; the entry aisle and parked row stay clear of both exit aisles. The yard
        # geometry is derived from the depot's bays in metres.
        spacing = 3 * SessionActor.SEPARATION_M
        count = len(self.data["vehicle_assignments"])
        self.parking_exit_aisles, self.parking_aisle_y, self.parking_positions = {}, {}, {}
        for depot in self.depots:
            bays_y = min(p[1] for p in depot["bays"].values())
            for index, charger_id in enumerate(depot["bays"]):
                self.parking_exit_aisles[charger_id] = bays_y-(index+1)*2*SessionActor.SEPARATION_M
            aisle_y = min(self.parking_exit_aisles[c] for c in depot["bays"])-2*SessionActor.SEPARATION_M
            self.parking_aisle_y[depot["depot_id"]] = aisle_y
            self.parking_positions[depot["depot_id"]] = {
                index: (depot["position_m"][0] + (index-(count-1)/2)*spacing, aisle_y - 2*SessionActor.SEPARATION_M)
                for index in range(count)}

    def get(self):
        return self.graph

    @lru_cache(maxsize=64)
    def _compiled(self, ids: tuple[str, ...]):
        edges = self.network["edges"]
        config = self.data["route_edit"]
        approaches = config["stop_approach_edge_ids"]
        pending = list(config["required_stops"].items())
        points = [tuple(edges[ids[0]]["path"][0])]
        travelled = 0.0
        stops = []
        for previous, identifier in zip((None, *ids), ids):
            edge = edges[identifier]
            if previous is not None and edges[previous]["target"] != edge["source"]:
                raise ValueError("Integrated route geometry is discontinuous")
            for point in edge["path"][1:]:
                point = tuple(point)
                travelled += math.dist(points[-1], point)
                points.append(point)
            if pending and edge["target"] == pending[0][1] and identifier in approaches[pending[0][0]]:
                stops.append((pending.pop(0)[0], travelled))
        return geometry(config["route_id"], points), tuple(stops)

    def compile_route(self, ids: list[str]):
        """The exact geometry the shuttle drives for a route, and how far along it each stop is."""
        path, stops = self._compiled(tuple(ids))
        return path, [stop for stop in stops]

    def lies_on(self, x: float, y: float, branch: str, tolerance_m: float = 0.01) -> bool:
        """Whether a point is on a named road chain of the fixture (main, bypass-b, bypass-c, return)."""
        for identifier in self.network["chains"][branch]:
            path = self.network["edges"][identifier]["path"]
            for (ax, ay), (bx, by) in zip(path, path[1:]):
                dx, dy = bx-ax, by-ay
                share = max(0.0, min(1.0, ((x-ax)*dx + (y-ay)*dy) / (dx*dx + dy*dy)))
                if math.hypot(ax + dx*share - x, ay + dy*share - y) <= tolerance_m:
                    return True
        return False

    def __deepcopy__(self, memo):
        # The actor never mutates the definition. Domain preview caches have
        # their own lock and must not be copied into controller staging.
        return self


@lru_cache
def get_workflow_definition() -> WorkflowDefinition:
    return WorkflowDefinition()


def services_for(snapshot: FullSnapshot, routes=None, incidents=None):
    """Select the same services for live sessions and recovered/archived logs."""
    if snapshot.scenario.scenario_id == SCENARIO_ID:
        definition = get_workflow_definition()
        return definition.routes, definition.incidents
    return routes, incidents


class IntegratedSessionActor(SessionActor):
    """One BE-M2-02 controller with recoverable route/incident/charging effects.

    A new session drives its shuttles through the area's background traffic (src.traffic.city_traffic),
    which it owns and checkpoints with the rest of its state. In the traffic a shuttle drives as a road
    vehicle: it keeps to its lane, accelerates and brakes, slows for bends, follows the vehicle in front,
    gives way, and brakes evenly to stand at its stops and at red lights. Without the traffic it keeps the
    planar controller's idealised motion: full speed or standing still. TRAFFIC chooses for new sessions;
    a restored session keeps whichever it was checkpointed with.
    """

    TRAFFIC = True

    def __init__(self, seed: int, *, session_id: str):
        self.definition = get_workflow_definition()
        self.traffic = None
        super().__init__(seed, self.definition.actor_routes, session_id=session_id)
        # Session-local, deterministic incident instance. Do not mutate the shared
        # WorkflowDefinition/IncidentService (used by all concurrent sessions).
        self.incident_catalog = self.definition.incidents.simulated_catalog
        self.incident_due_tick = round(self.rng.uniform(
            self.incident_catalog.policy["first_min_s"],
            self.incident_catalog.policy["first_max_s"]) / self.STEP_S)
        self.random_incident = None
        self.random_incidents = []
        logger.debug("[INCIDENT DEBUG] session=%s first_due_tick=%s", session_id, self.incident_due_tick)
        self.stop_names = {}
        self.loop_started = {}
        data = self.definition.data
        pin = hashlib.sha256(f"{self.definition.signature}:{seed}".encode()).hexdigest()[:24]
        self.scenario_revision = f"fleet-scenario-{pin}"
        self.scene_revision = f"fleet-scene-{self.definition.signature[:24]}"
        self.modes = {s.ID: "STANDBY" for s in self.shuttles}
        self.charge_elapsed = {}
        self.charge_started_kwh = {}
        self.parking_chargers = {}
        self.yard_claims = {}  # who was let into each depot's yard this tick; never outlives the tick
        self.safety_unavailable = set()
        self.continued = set()
        self.set_downs = {}
        self.tails = {}
        self.pending_events = []
        self.stop_progress = {}
        for shuttle, route, progress, battery in zip(self.shuttles, data["vehicle_assignments"],
                data["initial_progress_m"], data["initial_battery_kwh"]):
            shuttle.route_ID = route
            shuttle.progress = float(progress)
            shuttle.position = self._route_evaluation(route, progress)
            shuttle.battery = battery * 100 / self.BATTERY_CAPACITY_KWH
            shuttle.cruise_vel = data["speed_mps"]
            shuttle.curr_vel = 0
            shuttle.state = ShuttleState.DEPOT_STANDBY
            shuttle.dwell_timer = 0
        base = super().generate_snapshot().model_dump(mode="json", exclude={"state_hash"})
        self.domain = FullSnapshot.model_validate(base)
        self.domain.transform_revision = "integrated-planar-r1"
        self.domain.scenario.transform_revision = self.domain.transform_revision
        self.domain.scenario.scenario_id = SCENARIO_ID
        self.domain.scenario.rubric_revision = self.definition.incidents.config["rubric_revision"]
        self.domain.session.status = "READY"
        self.is_paused = True
        primary = self.domain.routes[0]
        primary.directed_edge_ids = data["route_edit"]["original_directed_edge_ids"]
        # The primary route now runs on road-graph edges, so its drawable paths come from the graph's
        # node positions: edge i runs from node i to node i + 1 of the walked path
        primary.edge_paths_m = [[Position(x_m=x, y_m=y, z_m=0) for x, y in self.definition.network["edges"][identifier]["path"]]
                                for identifier in primary.directed_edge_ids]
        primary.required_stop_ids = list(data["route_edit"]["required_stops"])
        primary.stops_m = [RouteStop(stop_id=stop, position=Position(x_m=float(data["nodes"][str(node)][0]),
                                                                     y_m=float(data["nodes"][str(node)][1]), z_m=0))
                           for stop, node in data["route_edit"]["required_stops"].items()]
        # Each peer shuttle has its own route and stops. Its loop starts and ends at the first stop,
        # where the loop's end already pulls in; the rest are places to stop along the way.
        for route in self.domain.routes:
            stops = data["peer_route_stops"].get(route.route_id)
            if stops is not None:
                route.required_stop_ids = [stop["stop_id"] for stop in stops]
                route.stops_m = [RouteStop(stop_id=stop["stop_id"], position=Position(x_m=stop["x"], y_m=stop["y"], z_m=0)) for stop in stops]
                self.stop_progress[route.route_id] = [stop["progress_m"] for stop in stops[1:]]
                self.stop_names[route.route_id] = {str(stop["progress_m"]): stop["stop_id"] for stop in stops[1:]}
                self.stop_names[route.route_id]["end"] = stops[0]["stop_id"]
        for route in self.domain.routes:
            route.estimated_duration_s = route.distance_m / data["speed_mps"] + self.DWELL_S
        primary.estimated_duration_s += data["route_edit"]["loop_end_dwell_s"]
        self.domain.disruptions = [Disruption(disruption_id="closure-m2", aggregate_revision=1,
            affected_edge_ids=data["route_edit"]["closed_directed_edge_ids"], active_from_tick=0,
            reason="Authored permanent main-road closure; approve a pre-trip bypass", provenance=self.domain.source)]
        self.domain.chargers = [Charger(charger_id=key, aggregate_revision=1, port_count=1,
            power_kw=50, occupied_vehicle_ids=[], position=Position(x_m=bay[0], y_m=bay[1], z_m=0))
            for key, bay in self.definition.bay_positions.items()]
        self.domain.depots = [Depot(depot_id=depot["depot_id"],
            position=Position(x_m=depot["position_m"][0], y_m=depot["position_m"][1], z_m=0),
            charger_ids=list(depot["bays"])) for depot in self.definition.depots]
        self._adopt_route(primary.route_id, primary.directed_edge_ids)
        self.domain = self.generate_snapshot()
        # How each shuttle was held this step, for the traffic around it; never outlives the step
        self.traffic_holds = {}
        if self.TRAFFIC:
            self.traffic = CityTraffic.start(SIGNAL_AREA)
            if self.traffic is not None:
                self._place_in_traffic({})
                self.traffic.clear_shuttles()

    def __deepcopy__(self, memo):
        """A staged session copies its actor every tick, so the copy shares the route geometry, which is
        never changed in place: a path (its dict and its waypoints) is only replaced or dropped
        (_adopt_route, _head_for_bay, synchronize), and so is each of the domain's routes, which a
        command or incident edits in a snapshot copy that synchronize then installs. Everything else,
        the domain's route list included, is copied as deepcopy would."""
        for path in (*self._routes.values(), *self.route):
            memo[id(path)] = path
            memo[id(path["waypoints"])] = path["waypoints"]
        for route in self.domain.routes:
            memo[id(route)] = route
        copy = type(self).__new__(type(self))
        memo[id(self)] = copy
        for name, value in self.__dict__.items():
            copy.__dict__[name] = deepcopy(value, memo)
        return copy

    def _validated_routes(self, route):
        return validate_routes(route, MAX_ROUTE_WAYPOINTS)

    def _adopt_route(self, route_id: str, ids: list[str]) -> None:
        """Drive a route exactly as approved, and pull in at each of its stops."""
        path, stops = self.definition.compile_route(ids)
        self._routes[route_id] = {**path, "route_ID": route_id}
        self.stop_progress[route_id] = [progress for _, progress in stops]
        self.stop_names[route_id] = {str(progress): stop for stop, progress in stops}

    def _note_stop(self, shuttle) -> None:
        """A shuttle has pulled in: record what it served, for the outcome of the operator's decision."""
        names = self.stop_names.get(shuttle.route_ID, {})
        stop = next((name for progress, name in names.items()
                     if progress != "end" and abs(shuttle.progress-float(progress)) < EPSILON), None)
        if stop is None and shuttle.progress >= self._routes[shuttle.route_ID]["total_length"]-EPSILON:
            stop = names.get("end")
        if stop is not None:
            self._event(shuttle, "STOP_SERVED", stop_id=stop, route_id=self.domain.vehicles[shuttle.ID].route_id)

    def _note_loop_finished(self, shuttle) -> None:
        started = self.loop_started.get(shuttle.ID)
        level = shuttle.battery*self.BATTERY_CAPACITY_KWH/100
        if started is not None:
            self._event(shuttle, "LOOP_COMPLETED", route_id=self.domain.vehicles[shuttle.ID].route_id,
                        duration_s=round((self.current_tick-started[0])*self.STEP_S, 3),
                        energy_kwh=round(started[1]-level, 6))
        self.loop_started[shuttle.ID] = (self.current_tick, level)

    def _state_hash(self):
        if not hasattr(self, "domain"):
            return super()._state_hash()
        state = self.workflow_state()
        state["domain"].pop("snapshot_id", None)
        state["domain"].pop("event_sequence", None)
        for event in state["pending_events"]:
            event.pop("event_id", None)
        return hashlib.sha256(json.dumps({"state": state, "tick": self.current_tick,
            "shuttles": [s.model_dump(mode="json") for s in self.shuttles]}, sort_keys=True).encode()).hexdigest()

    def _set_mode(self, shuttle, mode):
        if self.modes[shuttle.ID] != mode:
            self.modes[shuttle.ID] = mode
            self.domain.vehicles[shuttle.ID].aggregate_revision += 1

    def _event(self, shuttle, name, **data):
        vehicle = self.domain.vehicles[shuttle.ID]
        self.pending_events.append(Event(event_id=uuid4().hex, event_sequence=0, tick=self.current_tick,
            event_type=name, entity_type="VEHICLE", entity_id=vehicle.vehicle_id,
            entity_revision=vehicle.aggregate_revision, source=self.domain.source,
            data={"transition_tick": self.current_tick, **data}))

    def _yard_points(self, start, destination, *, aisle_y):
        if math.dist(start, destination) < EPSILON:
            return [tuple(start)]
        points = [tuple(start), (start[0], aisle_y), (destination[0], aisle_y), tuple(destination)]
        return [p for index, p in enumerate(points) if index == 0 or p != points[index-1]]

    def _parking_points(self, shuttle_id, charger_id):
        depot_id = self.definition.charger_depot[charger_id]["depot_id"]
        return self._yard_points(self.definition.bay_positions[charger_id],
                                 self.definition.parking_positions[depot_id][shuttle_id],
                                 aisle_y=self.definition.parking_exit_aisles[charger_id])

    def _begin_parking(self, shuttle, charger_id, *, release_port=True):
        self.parking_chargers[shuttle.ID] = charger_id if release_port else None
        points = self._leaving_points(shuttle, charger_id) if release_port and self._back_to_work() else self._parking_points(shuttle.ID, charger_id)
        path = geometry(f"depot-path-{shuttle.ID}", points)
        self._routes[path["route_ID"]] = path
        shuttle.route_ID, shuttle.progress = path["route_ID"], 0.0
        self._set_mode(shuttle, "RETURNING_TO_DEPOT")
        shuttle.curr_vel, shuttle.state = shuttle.cruise_vel, ShuttleState.ROUTE_LOOP

    def _back_to_work(self):
        """A shuttle charged to full has no reason to park: it clears the bay and rejoins its route."""
        return bool(self.definition.data.get("charge_time_scale"))

    def _yard_entry_m(self, shuttle):
        """How far along its path a shuttle driving in waits for its depot's yard, or None if it holds no port."""
        port = self._held_charger(shuttle)
        if port is None:
            return None
        bay, road = self.definition.bay_positions[port.charger_id], self._connector(shuttle, port.charger_id)[-1]
        total = self._routes[shuttle.route_ID]["total_length"]
        return max(0.0, total-(abs(road[0]-bay[0])+abs(road[1]-bay[1]))-YARD_WAIT_M)

    def _in_yard(self, shuttle):
        """Whether this shuttle is on the move inside a depot's yard."""
        if self.modes[shuttle.ID] != "RETURNING_TO_DEPOT" or not shuttle.route_ID.startswith("depot-path-"):
            return False
        if shuttle.ID in self.parking_chargers:
            return shuttle.progress > EPSILON
        entry = self._yard_entry_m(shuttle)
        return entry is not None and shuttle.progress > entry+EPSILON

    def _yard_depot(self, shuttle):
        position = (shuttle.position.x, shuttle.position.y)
        return min(self.definition.depots, key=lambda depot: math.dist(position, depot["position_m"]))["depot_id"]

    def _may_enter_yard(self, shuttle, *, leaving=False):
        """A shuttle driving in shares its lane with everything else in the yard, so it moves there alone;
        those driving out each have their own aisle, so may leave together. One in the yard is on its way
        to a stop and never waits, so the yard always clears."""
        depot = self._yard_depot(shuttle)
        claim = self.yard_claims.get(depot)
        if claim is not None and claim[0] != shuttle.ID and not (leaving and claim[1]):
            return False
        if any(other.ID != shuttle.ID and self._in_yard(other) and self._yard_depot(other) == depot
               and not (leaving and other.ID in self.parking_chargers) for other in self.shuttles):
            return False
        self.yard_claims[depot] = (shuttle.ID, leaving)
        return True

    def _conflicts(self, first, first_end, second, second_end):
        # Lanes are not modelled, so a charging trip that meets traffic on the same road would hold
        # both shuttles nose to nose for good. Charging trips pass as if in the other lane; in a
        # depot's yard they take turns instead (_may_enter_yard).
        if first.route_ID.startswith("depot-path-") or second.route_ID.startswith("depot-path-"):
            return False
        return super()._conflicts(first, first_end, second, second_end)

    def _connector(self, shuttle, charger_id, kind="connectors"):
        """The road from the start of this shuttle's route to the charger's depot, or with "exits" the road back."""
        route_id = self.domain.vehicles[shuttle.ID].route_id
        roads = self.definition.charger_depot[charger_id][kind]
        return roads[route_id if route_id in roads else self.definition.data["route_edit"]["route_id"]]

    def _leaving_points(self, shuttle, charger_id):
        """From the bay out to the road, where the way back to the route begins."""
        road = self._connector(shuttle, charger_id, "exits")[0]
        return self._yard_points(self.definition.bay_positions[charger_id], road, aisle_y=road[1])

    def _head_back_to_route(self, shuttle, charger_id):
        """Drives a charged shuttle along the depot's way out to the start of its route, in service."""
        start = self._route_evaluation(self.domain.vehicles[shuttle.ID].route_id, 0.0)
        points = [tuple(p) for p in self._connector(shuttle, charger_id, "exits")]
        if math.dist(points[-1], (start.x, start.y)) > EPSILON:
            points.append((start.x, start.y))  # an edited route may start somewhere else
        self._head_for_bay(shuttle, points)
        self._set_mode(shuttle, "IN_SERVICE")
        shuttle.state, shuttle.curr_vel = ShuttleState.ROUTE_LOOP, shuttle.cruise_vel
        self.loop_started.pop(shuttle.ID, None)

    def _rejoin_route(self, shuttle):
        """Back at the start of its route: the first stop's dwell, then a normal loop."""
        vehicle = self.domain.vehicles[shuttle.ID]
        shuttle.route_ID, shuttle.progress = vehicle.route_id, 0.0
        shuttle.position = self._route_evaluation(shuttle.route_ID, 0.0)
        self._set_mode(shuttle, "DWELLING")
        shuttle.dwell_timer, shuttle.curr_vel, shuttle.state = self.DWELL_S, 0, ShuttleState.STOP_DWELL
        finish_hold(self.domain, vehicle.vehicle_id)
        self._event(shuttle, "RETURNED_TO_ROUTE", route_id=vehicle.route_id)

    @staticmethod
    def _path_distance(points):
        return sum(math.dist(a, b) for a, b in zip(points, points[1:]))

    def _depot_points(self, shuttle, charger_id):
        if shuttle.route_ID.startswith("depot-path-"):
            # A parked shuttle is already in the yard. It must not repeat its
            # service-route connector when it needs another charge.
            depot_id = self.definition.charger_depot[charger_id]["depot_id"]
            return self._yard_points((shuttle.position.x, shuttle.position.y),
                                      self.definition.bay_positions[charger_id],
                                      aisle_y=self.definition.parking_aisle_y[depot_id])
        path = self._routes[shuttle.route_ID]
        points = [(shuttle.position.x, shuttle.position.y)]
        travelled = 0.0
        for a, b in zip(path["waypoints"], path["waypoints"][1:]):
            travelled += math.hypot(b.x-a.x, b.y-a.y)
            if travelled > shuttle.progress + EPSILON:
                points.append((b.x, b.y))
        points.extend(tuple(p) for p in self._connector(shuttle, charger_id))
        bay = self.definition.bay_positions[charger_id]
        points.extend([(bay[0], points[-1][1]), tuple(bay)])
        return [p for index, p in enumerate(points) if index == 0 or p != points[index-1]]

    def _held_charger(self, shuttle):
        vehicle_id = self.domain.vehicles[shuttle.ID].vehicle_id
        return next((c for c in self.domain.chargers if vehicle_id in c.occupied_vehicle_ids), None)

    def _nearest_depot(self, shuttle):
        return min(self.definition.depots,
                   key=lambda depot: math.dist((shuttle.position.x, shuttle.position.y), depot["position_m"]))

    def _arrival_kwh(self, level, distance_m, extra_s=0.0):
        """The charge left after driving distance_m at the declared speed and standing extra_s more.

        Below zero means the shuttle cannot get there at all.
        """
        return (level - drive_energy_kwh(ENERGY_R1, distance_m)
                - auxiliary_energy_kwh(ENERGY_R1, distance_m/self.definition.data["speed_mps"] + extra_s))

    def _route_tail(self, shuttle, depot):
        """Metres from the end of the shuttle's route to the furthest of the depot's bays.

        This is the fixed part of every way in from that route (see _depot_points), so it is worked
        out once for each route end and depot instead of every tick.
        """
        end = self._routes[shuttle.route_ID]["waypoints"][-1]
        route_id = self.domain.vehicles[shuttle.ID].route_id
        root = route_id if route_id in depot["connectors"] else self.definition.data["route_edit"]["route_id"]
        key = (end.x, end.y, root, depot["depot_id"])
        if key not in self.tails:
            lengths = []
            for charger_id in depot["bays"]:
                points = [(end.x, end.y), *(tuple(p) for p in depot["connectors"][root])]
                bay = self.definition.bay_positions[charger_id]
                points.extend([(bay[0], points[-1][1]), tuple(bay)])
                lengths.append(self._path_distance(points))
            self.tails[key] = max(lengths)
        return self.tails[key]

    def _depot_distance(self, shuttle, depot):
        # The longest way in to any of its bays, so the estimate holds whichever bay is free
        if shuttle.route_ID.startswith("depot-path-"):
            return max(self._path_distance(self._depot_points(shuttle, charger_id)) for charger_id in depot["bays"])
        # The rest of the route, then the depot's road in from its end
        remaining = max(0.0, self._routes[shuttle.route_ID]["total_length"]-shuttle.progress)
        return remaining + self._route_tail(shuttle, depot)

    def _open_depots(self, shuttle):
        """(distance, depot) for every depot with a free charger, nearest first."""
        free = {c.charger_id for c in self.domain.chargers if len(c.occupied_vehicle_ids) < c.port_count}
        ranked = sorted(((self._depot_distance(shuttle, depot), index) for index, depot in enumerate(self.definition.depots)
                         if free & set(depot["bays"])))
        return [(distance, self.definition.depots[index]) for distance, index in ranked]

    def depot_choice(self, shuttle):
        """The depot a shuttle would head for, and its road distance there in metres.

        A shuttle bound for or at a bay keeps that bay's depot, and one parked in a yard
        stays at that yard. Otherwise it is the nearest depot with a free charger, or the
        nearest depot when every charger is taken.
        """
        mode = self.modes[shuttle.ID]
        held = self._held_charger(shuttle)
        if mode == "RETURNING_TO_DEPOT":
            charger_id = held.charger_id if held is not None else self.parking_chargers.get(shuttle.ID)
            depot = self.definition.charger_depot[charger_id] if charger_id else self._nearest_depot(shuttle)
            return depot, max(0.0, self._routes[shuttle.route_ID]["total_length"]-shuttle.progress)
        if mode in {"QUEUED_FOR_CHARGE", "CHARGING"} and held is not None:
            return self.definition.charger_depot[held.charger_id], 0.0
        if shuttle.route_ID.startswith("depot-path-"):
            depot = self._nearest_depot(shuttle)
            return depot, self._depot_distance(shuttle, depot)
        open_depots = self._open_depots(shuttle)
        if open_depots:
            distance, depot = open_depots[0]
            return depot, distance
        depot = min(self.definition.depots, key=lambda d: self._depot_distance(shuttle, d))
        return depot, self._depot_distance(shuttle, depot)

    def _guarded_depots(self, shuttle):
        """The depots a serving shuttle must keep in reach above its reserve.

        That is every depot with a free charger it can still reach above its reserve, not only
        the nearest: if the nearest fills up, the next one is then still in reach. A depot already
        out of reach, as for a shuttle that starts low, cannot be kept and is left out. With none
        in reach, the nearest depot with a free charger, or else the nearest depot.
        """
        level = shuttle.battery*self.BATTERY_CAPACITY_KWH/100
        reserve = reserve_kwh(ENERGY_R1, self.BATTERY_CAPACITY_KWH)
        open_depots = self._open_depots(shuttle)
        in_reach = [depot for distance, depot in open_depots if self._arrival_kwh(level, distance) >= reserve]
        return in_reach or [depot for _, depot in open_depots[:1]] or [self.depot_choice(shuttle)[0]]

    def _depot_trip(self, shuttle, charger_id, level):
        """The way from where the shuttle is to a charger's bay, its length, and the charge it would arrive with."""
        points = self._depot_points(shuttle, charger_id)
        distance = self._path_distance(points)
        return points, distance, self._arrival_kwh(level, distance)

    def _head_for_bay(self, shuttle, points):
        """Puts the shuttle on its way to a bay it already holds, along points from _depot_trip."""
        if len(points) > 1:
            path = geometry(f"depot-path-{shuttle.ID}", points)
            self._routes[path["route_ID"]] = path
            shuttle.route_ID, shuttle.progress = path["route_ID"], 0.0

    def _finish_loop(self, shuttle):
        """Records a finished loop, and the end of an approved detour that was its route."""
        self._note_loop_finished(shuttle)
        if any(i.vehicle_id == self.domain.vehicles[shuttle.ID].vehicle_id and i.state == "EXECUTING" for i in self.domain.incidents):
            self._event(shuttle, "DETOUR_COMPLETED")

    def _set_down_stop(self, shuttle):
        """Where a shuttle turning back lets its riders off, and the stops it then misses.

        It is the stop the shuttle stands at, or else the next one ahead on its route (the loop's
        end counts as a stop). The way to the depot runs on along the route, so that stop is on
        the way. Returns (metres to it, its stop ID, the IDs of the later stops on this loop).
        """
        names = self.stop_names.get(shuttle.route_ID, {})
        total = self._routes[shuttle.route_ID]["total_length"]
        stops = sorted({**{p: names.get(str(p)) for p in self.stop_progress.get(shuttle.route_ID, [])},
                        **({total: names["end"]} if "end" in names else {total: names.get(str(total))})}.items())
        at, stop_id = next(((p, n) for p, n in stops if p >= shuttle.progress-EPSILON), stops[-1])
        missed = [n for p, n in stops if p > at+EPSILON and n is not None]
        return max(0.0, at-shuttle.progress), stop_id, missed

    def note_decisions(self, events):
        """Remembers which shuttles' latest battery decision was CONTINUE, from a commit's events.

        A safety return after CONTINUE is the consequence of that decision, which its own
        SERVICE_CONTINUED event scores, not a decision nobody made.
        """
        for event in events:
            if event.event_type == "SERVICE_CONTINUED":
                self.continued.add(event.entity_id)
            elif event.event_type == "CHARGER_RESERVED":
                self.continued.discard(event.data.get("vehicle_id"))

    def _safety_returns(self):
        """Send a shuttle back on its own at the last point it can still reach a charger above its reserve.

        The check is kept against every depot with a free charger still in reach (see
        _guarded_depots), so a nearer depot filling up never leaves the shuttle past its safe
        point for the next one; it then heads for the nearest of them. At a loop end it also
        turns back when another loop would take it past that point, and the finished loop is
        recorded first. A shuttle on an approved detour leaves it, and the incident service
        resolves the incident.

        It does not drive to the depot with riders on board. It stops serving its route at once
        (the SAFETY_RETURN event is the low-battery warning), lets everyone off at the stop it
        stands at or the next one ahead (PASSENGERS_SET_DOWN after one dwell), then drives to the
        depot empty; the event lists the stops it misses on the rest of that loop. That stop is on
        the way in, so the only extra cost is the dwell, which the check allows for.

        The SAFETY_RETURN event scores a missed decision as zero; after CONTINUE it carries no
        score of its own, since SERVICE_CONTINUED scored that choice. The shuttle takes the same
        trip and path as a CHARGE_NOW (_depot_trip, _head_for_bay). Unlike CHARGE_NOW it goes even
        if it would arrive below its reserve (after CONTINUE past the safe point, or for a shuttle
        that started out of reach), since
        reaching a charger beats stopping on the road; it does not go if it cannot arrive at all.
        With no free charger, or none in reach, it carries on and SAFETY_RETURN_UNAVAILABLE is
        logged once; 0 kWh stays the only hard floor.
        """
        speed = self.definition.data["speed_mps"]
        reserve = reserve_kwh(ENERGY_R1, self.BATTERY_CAPACITY_KWH)
        # Two ticks of driving, so the check fires before the margin is gone
        buffer = 2*(drive_energy_kwh(ENERGY_R1, speed*self.STEP_S)+auxiliary_energy_kwh(ENERGY_R1, self.STEP_S))
        for shuttle in self.shuttles:
            mode = self.modes[shuttle.ID]
            if (mode not in {"IN_SERVICE", "DWELLING", "DETOURING"}
                    or shuttle.route_ID.startswith("depot-path-") or self._held_charger(shuttle) is not None):
                continue
            level = shuttle.battery*self.BATTERY_CAPACITY_KWH/100
            if level <= 0:
                continue
            route = self._routes[shuttle.route_ID]
            at_loop_end = mode == "DWELLING" and shuttle.progress >= route["total_length"]-EPSILON
            # Leaving for another loop commits it to the whole loop before the next way in
            loop_m, loop_s = 0.0, 0.0
            if at_loop_end:
                loop_m = route["total_length"]
                loop_s = shuttle.dwell_timer + (len(self.stop_progress.get(shuttle.route_ID, []))+1)*self.DWELL_S
            guarded = self._guarded_depots(shuttle)
            # The extra dwell is where it lets its riders off on the way in
            worst = min(self._arrival_kwh(level, self._depot_distance(shuttle, depot)+loop_m, loop_s+self.DWELL_S) for depot in guarded)
            if worst-reserve > buffer:
                self.safety_unavailable.discard(shuttle.ID)
                continue
            vehicle = self.domain.vehicles[shuttle.ID]
            open_depots = self._open_depots(shuttle)
            charger = next((c for c in sorted(self.domain.chargers, key=lambda c: c.charger_id)
                            if open_depots and c.charger_id in open_depots[0][1]["bays"]
                            and len(c.occupied_vehicle_ids) < c.port_count), None)
            points, distance, arrival = self._depot_trip(shuttle, charger.charger_id, level) if charger else (None, None, None)
            if charger is not None:
                arrival -= auxiliary_energy_kwh(ENERGY_R1, self.DWELL_S)  # standing to let riders off
            if charger is None or arrival <= 0:
                if shuttle.ID not in self.safety_unavailable:
                    self.safety_unavailable.add(shuttle.ID)
                    self._event(shuttle, "SAFETY_RETURN_UNAVAILABLE", battery_kwh=level,
                                reason="NO_FREE_CHARGER" if charger is None else "CHARGER_OUT_OF_REACH",
                                depot_id=(open_depots[0][1] if open_depots else guarded[0])["depot_id"])
                continue
            self.safety_unavailable.discard(shuttle.ID)
            set_down_m, set_down_stop, missed = self._set_down_stop(shuttle)
            if at_loop_end:
                self._finish_loop(shuttle)
                self.loop_started.pop(shuttle.ID, None)  # the trip in is not the next loop
            charger.occupied_vehicle_ids.append(vehicle.vehicle_id)
            charger.aggregate_revision += 1
            record_hold(self.domain, vehicle.vehicle_id, charger.charger_id, self.current_tick,
                        distance/speed + self._charge_seconds(arrival, charger.power_kw), "SAFETY_RETURN")
            self._head_for_bay(shuttle, points)
            # The depot path starts here and runs on along the route, so the stop is as far along it
            self.set_downs[shuttle.ID] = {"at_m": set_down_m, "stop_id": set_down_stop, "timer": self.DWELL_S}
            self._set_mode(shuttle, "RETURNING_TO_DEPOT")
            shuttle.state, shuttle.curr_vel = ShuttleState.ROUTE_LOOP, shuttle.cruise_vel
            continued = vehicle.vehicle_id in self.continued
            self.continued.discard(vehicle.vehicle_id)
            if continued:
                assessment = None  # SERVICE_CONTINUED scored the decision this follows from
            else:
                assessment = {"rubric_revision": RUBRIC_REVISION, "provisional": True, "uncalibrated": True,
                    "scope": "BATTERY_DECISION", "outcome": "MISSED_DECISION",
                    "criteria": {key: 0 for key in RUBRIC}, "maximum_points": RUBRIC, "total": 0,
                    "assumptions": ["No charging decision was made before the last safe point to turn "
                                    "for the depot; the shuttle returned automatically."]}
            self._event(shuttle, "SAFETY_RETURN", battery_kwh=level, reserve_kwh=reserve, expected_arrival_kwh=arrival,
                depot_id=self.definition.charger_depot[charger.charger_id]["depot_id"], charger_id=charger.charger_id,
                distance_m=distance, after_decision="CONTINUE" if continued else None,
                left_detour=mode == "DETOURING", set_down_stop_id=set_down_stop, stops_missed=missed,
                assessment=assessment)

    def _charge_seconds(self, level, power_kw):
        """How long a charge from this level lasts: to full in training time, or the scenario's fixed time."""
        scale = self.definition.data.get("charge_time_scale")
        if not scale:
            return self.definition.data["charge_s"]
        return seconds_to_full(ENERGY_R1, level, self.BATTERY_CAPACITY_KWH, power_kw, scale)

    def _charge_finished(self, shuttle_id, level):
        if self.definition.data.get("charge_time_scale"):
            return level >= self.BATTERY_CAPACITY_KWH-FULL_TOLERANCE_KWH
        return self.charge_elapsed[shuttle_id] >= self.definition.data["charge_s"]-EPSILON

    def _publish_trip(self, booking, shuttle):
        """Says what a shuttle holding a port is doing now, and publishes the way it is driving."""
        mode = self.modes[shuttle.ID]
        if mode in {"QUEUED_FOR_CHARGE", "CHARGING"}:
            booking.phase, path = "CHARGING", None
        elif mode in {"IN_SERVICE", "DETOURING"} and shuttle.route_ID.startswith("depot-path-"):
            booking.phase, path = "RETURNING_TO_ROUTE", self._routes[shuttle.route_ID]["waypoints"]
        elif mode != "RETURNING_TO_DEPOT":
            booking.phase, path = None, None
        else:
            booking.phase = ("LEAVING_BAY" if shuttle.ID in self.parking_chargers
                else "DROPPING_PASSENGERS" if shuttle.ID in self.set_downs else "DRIVING_TO_CHARGER")
            route = self._routes[shuttle.route_ID]
            path = route["waypoints"] if shuttle.route_ID.startswith("depot-path-") else None
        booking.set_down_stop_id = self.set_downs[shuttle.ID]["stop_id"] if shuttle.ID in self.set_downs else None
        trip = [[p.x, p.y] for p in path] if path else []
        if trip != booking.trip_path_m:
            booking.trip_path_m = trip

    def _miss_bookings(self, shuttle):
        """A shuttle that ran out can no longer use any slot it holds."""
        vehicle_id = self.domain.vehicles[shuttle.ID].vehicle_id
        for booking in live_bookings(self.domain):
            if booking.vehicle_id == vehicle_id:
                settle(self.domain, booking, "MISSED", "BATTERY_EXHAUSTED")
                self._event(shuttle, "CHARGE_BOOKING_MISSED", booking_id=booking.booking_id, reason="BATTERY_EXHAUSTED")

    def _run_bookings(self):
        """Start booked slots when their time comes, and keep overrunning ones on the schedule.

        A due slot sends its shuttle in the way a CHARGE_NOW does, riders off first. It waits while
        the port is still held by an earlier overrun or the shuttle cannot leave (held by traffic or
        an incident), and is MISSED if that lasts past the end of its window. The port is only taken
        at dispatch, so a booking alone never occupies a bay.
        """
        by_vehicle = {self.domain.vehicles[s.ID].vehicle_id: s for s in self.shuttles}
        for booking in sorted(live_bookings(self.domain), key=lambda b: (b.start_tick, b.booking_id)):
            shuttle = by_vehicle[booking.vehicle_id]
            if booking.status == "ACTIVE":
                if self.current_tick >= booking.end_tick:
                    booking.end_tick = self.current_tick + 1
                self._publish_trip(booking, shuttle)
                continue
            if self.current_tick < booking.start_tick:
                continue
            if self.current_tick >= booking.end_tick:
                port = next(c for c in self.domain.chargers if c.charger_id == booking.charger_id)
                reason = "PORT_BUSY" if len(port.occupied_vehicle_ids) >= port.port_count else "SHUTTLE_NOT_READY"
                settle(self.domain, booking, "MISSED", reason)
                self._event(shuttle, "CHARGE_BOOKING_MISSED", booking_id=booking.booking_id, reason=reason)
                continue
            self._start_booking(booking, shuttle)

    def _start_booking(self, booking, shuttle):
        mode = self.modes[shuttle.ID]
        vehicle = self.domain.vehicles[shuttle.ID]
        port = next(c for c in self.domain.chargers if c.charger_id == booking.charger_id)
        if (mode not in {"IN_SERVICE", "DWELLING", "STANDBY"} or self._held_charger(shuttle) is not None
                or len(port.occupied_vehicle_ids) >= port.port_count):
            return
        level = shuttle.battery*self.BATTERY_CAPACITY_KWH/100
        points, distance, arrival = self._depot_trip(shuttle, port.charger_id, level)
        if arrival <= 0:
            settle(self.domain, booking, "MISSED", "CHARGER_OUT_OF_REACH")
            self._event(shuttle, "CHARGE_BOOKING_MISSED", booking_id=booking.booking_id, reason="CHARGER_OUT_OF_REACH")
            return
        serving = mode != "STANDBY" and not shuttle.route_ID.startswith("depot-path-")
        set_down_m, set_down_stop, missed = self._set_down_stop(shuttle) if serving else (None, None, [])
        if serving and mode == "DWELLING" and shuttle.progress >= self._routes[shuttle.route_ID]["total_length"]-EPSILON:
            self._finish_loop(shuttle)
            self.loop_started.pop(shuttle.ID, None)  # the trip in is not the next loop
        port.occupied_vehicle_ids.append(vehicle.vehicle_id)
        port.aggregate_revision += 1
        self._head_for_bay(shuttle, points)
        if serving:
            self.set_downs[shuttle.ID] = {"at_m": set_down_m, "stop_id": set_down_stop, "timer": self.DWELL_S}
        self._set_mode(shuttle, "RETURNING_TO_DEPOT")
        shuttle.state, shuttle.curr_vel = ShuttleState.ROUTE_LOOP, shuttle.cruise_vel
        self.continued.discard(vehicle.vehicle_id)
        self._publish_trip(booking, shuttle)
        # A late start keeps the slot's length; the schedule shows the shifted window
        booking.end_tick += self.current_tick - booking.start_tick
        booking.start_tick = self.current_tick
        booking.status = "ACTIVE"
        booking.revision += 1
        self._event(shuttle, "CHARGE_BOOKING_STARTED", booking_id=booking.booking_id, charger_id=port.charger_id,
                    distance_m=distance, expected_arrival_kwh=arrival, set_down_stop_id=set_down_stop, stops_missed=missed)

    def depot_distances(self):
        return {self.domain.vehicles[shuttle.ID].vehicle_id: self.depot_choice(shuttle)[1] for shuttle in self.shuttles}

    def battery_decisions(self, model, snapshot, *, in_place=False):
        choices = {self.domain.vehicles[shuttle.ID].vehicle_id: self.depot_choice(shuttle) for shuttle in self.shuttles}
        # From each loop's end, the way in to the furthest depot it must keep in reach (see _safety_returns)
        returns = {self.domain.vehicles[shuttle.ID].vehicle_id: max(self._route_tail(shuttle, depot) for depot in self._guarded_depots(shuttle))
                   for shuttle in self.shuttles
                   if self.modes[shuttle.ID] in {"IN_SERVICE", "DWELLING", "DETOURING"} and not shuttle.route_ID.startswith("depot-path-")}
        return with_battery_decisions(model, snapshot, self.definition.data["charge_s"],
            charge_time_scale=self.definition.data.get("charge_time_scale"),
            depot_distances_m={vehicle_id: distance for vehicle_id, (_, distance) in choices.items()},
            depot_speed_mps=self.definition.data["speed_mps"],
            parking_distances_m={(vehicle.vehicle_id, charger_id): self._path_distance(self._parking_points(shuttle.ID, charger_id))
                for shuttle, vehicle in zip(self.shuttles, snapshot.vehicles)
                for charger_id in self.definition.bay_positions},
            depot_choices={vehicle_id: depot["depot_id"] for vehicle_id, (depot, _) in choices.items()},
            return_distances_m=returns, in_place=in_place)

    def generate_snapshot(self, *, in_place: bool = False) -> FullSnapshot:
        """The public snapshot of where the session is now: a copy of the domain, or with in_place the
        domain itself, filled in (for a caller about to replace the domain with it)."""
        result = self.domain if in_place else self.domain.model_copy(deep=True)
        result.tick, result.simulation_time_s = self.current_tick, self.current_tick*self.STEP_S
        result.session_epoch = result.session.session_epoch = self.session_epoch
        result.session.aggregate_revision = self.command_revision
        for shuttle, vehicle in zip(self.shuttles, result.vehicles):
            vehicle.position = Position(x_m=shuttle.position.x, y_m=shuttle.position.y, z_m=0)
            vehicle.battery_kwh = round(shuttle.battery*self.BATTERY_CAPACITY_KWH/100, 12)
            vehicle.speed_mps = shuttle.curr_vel
            vehicle.service_state = self.modes[shuttle.ID]
            if shuttle.state == ShuttleState.BLOCKED and vehicle.service_state in {"IN_SERVICE", "DETOURING"}:
                vehicle.service_state = "BLOCKED"
            a = self._route_evaluation(shuttle.route_ID, max(0, shuttle.progress-.01))
            b = self._route_evaluation(shuttle.route_ID, shuttle.progress+.01)
            vehicle.heading_deg = math.degrees(math.atan2(b.y-a.y, b.x-a.x))
        # result is this call's own copy: fill it rather than copy it again
        return self.battery_decisions(ENERGY_R1, result, in_place=True)

    @staticmethod
    def _project(path, point):
        best = (float("inf"), 0.0)
        travelled = 0.0
        for a, b in zip(path["waypoints"], path["waypoints"][1:]):
            dx, dy = b.x-a.x, b.y-a.y
            length = math.hypot(dx, dy)
            t = max(0, min(1, ((point.x-a.x)*dx+(point.y-a.y)*dy)/(length*length)))
            error = math.hypot(point.x-a.x-t*dx, point.y-a.y-t*dy)
            if error < best[0]:
                best = (error, travelled+t*length)
            travelled += length
        if best[0] > .01:
            raise TrainingStoreError("INVALID_PAYLOAD", "Approved path does not connect to the shuttle position")
        return best[1]

    def synchronize(self, snapshot: FullSnapshot, *, adopt: bool = False):
        """Apply validated domain consequences to a staged controller, before commit. With adopt, the
        snapshot itself becomes the domain rather than a copy of it: for a caller that no longer uses it."""
        starting = self.domain.session.status == "READY" and snapshot.session.status == "RUNNING"
        old = {v.vehicle_id: v for v in self.domain.vehicles}
        known = {route.route_id: route.revision for route in self.domain.routes}
        for route in snapshot.routes:
            if known.get(route.route_id) != route.revision:
                self._adopt_route(route.route_id, route.directed_edge_ids)
        for shuttle, vehicle in zip(self.shuttles, snapshot.vehicles):
            prior = old[vehicle.vehicle_id]
            if ((vehicle.route_id, vehicle.route_revision) != (prior.route_id, prior.route_revision)
                    or abs(shuttle.position.x-vehicle.position.x_m) > EPSILON
                    or abs(shuttle.position.y-vehicle.position.y_m) > EPSILON):
                if vehicle.route_id not in self._routes:
                    raise TrainingStoreError("UNKNOWN_CONSTRAINT", "Route has no admitted actor path")
                shuttle.progress = self._project(self._routes[vehicle.route_id],
                    Vector2D(x=vehicle.position.x_m, y=vehicle.position.y_m))
                shuttle.route_ID = vehicle.route_id
            if starting:
                vehicle.service_state = "DWELLING" if shuttle.ID == 0 else "IN_SERVICE"
                vehicle.aggregate_revision += 1
                shuttle.dwell_timer = 0
            mode = vehicle.service_state
            if mode == "RETURNING_TO_DEPOT" and self.modes[shuttle.ID] != mode:
                port = next(c for c in snapshot.chargers if vehicle.vehicle_id in c.occupied_vehicle_ids)
                points, _, arrival = self._depot_trip(shuttle, port.charger_id, vehicle.battery_kwh)
                if arrival + EPSILON < reserve_kwh(ENERGY_R1, vehicle.battery_capacity_kwh):
                    raise TrainingStoreError("NO_FEASIBLE_ROUTE", "Admitted depot path would breach the battery reserve")
                self._head_for_bay(shuttle, points)
                self.continued.discard(vehicle.vehicle_id)
            traffic_hold = mode == "BLOCKED" and self.modes[shuttle.ID] in {"IN_SERVICE", "DETOURING"}
            if traffic_hold:
                mode = self.modes[shuttle.ID]  # Preserve the intended service mode during traffic holds.
            if shuttle.ID == 0 and mode != self.modes[shuttle.ID] and mode == "AWAITING_GUIDANCE":
                logger.debug("[INCIDENT HOLD] shuttle-01 entering AWAITING_GUIDANCE at tick=%s, "
                             "progress=%.2fm; current incident must be reviewed", self.current_tick, shuttle.progress)
            if shuttle.ID == 0 and self.modes[shuttle.ID] == "AWAITING_GUIDANCE" and mode == "IN_SERVICE":
                logger.debug("[INCIDENT HOLD] shuttle-01 released at tick=%s; "
                             "check incident expiry or operator decision", self.current_tick)
            changed = mode != self.modes[shuttle.ID] or starting
            self.modes[shuttle.ID] = mode
            if changed:
                shuttle.state = (ShuttleState.ROUTE_LOOP if mode in {"IN_SERVICE", "DETOURING", "RETURNING_TO_DEPOT"}
                    else ShuttleState.STOP_DWELL if mode == "DWELLING" else ShuttleState.CHARGING if mode == "CHARGING"
                    else ShuttleState.DEPOT_STANDBY)
                shuttle.curr_vel = self.definition.data["speed_mps"] if shuttle.state == ShuttleState.ROUTE_LOOP else 0.0
            shuttle.position = Vector2D(x=vehicle.position.x_m, y=vehicle.position.y_m)
            shuttle.battery = vehicle.battery_kwh*100/vehicle.battery_capacity_kwh
        # A detour replaced by a later one is never driven again, so its path leaves the checkpoint.
        driven = {v.route_id for v in snapshot.vehicles}
        for incident in snapshot.incidents:
            detour = incident.incident_id + "-detour"
            if detour not in driven:
                self._routes.pop(detour, None)
                self.stop_progress.pop(detour, None)
                self.stop_names.pop(detour, None)
        self.domain = snapshot if adopt else snapshot.model_copy(deep=True)
        self.command_revision = snapshot.session.aggregate_revision
        self.is_paused = snapshot.session.status != "RUNNING"

    def _prepare_motion(self, shuttle):
        mode = self.modes[shuttle.ID]
        if mode == "DWELLING":
            shuttle.curr_vel = 0
            shuttle.dwell_timer = max(0, shuttle.dwell_timer-self.STEP_S)
            if shuttle.dwell_timer < EPSILON:
                if shuttle.progress >= self._routes[shuttle.route_ID]["total_length"]-EPSILON:
                    shuttle.progress = 0.0
                    self._finish_loop(shuttle)
                detouring = any(i.vehicle_id == self.domain.vehicles[shuttle.ID].vehicle_id and i.state == "EXECUTING"
                    for i in self.domain.incidents) and shuttle.progress != 0
                self._set_mode(shuttle, "DETOURING" if detouring else "IN_SERVICE")
                shuttle.state, shuttle.curr_vel = ShuttleState.ROUTE_LOOP, shuttle.cruise_vel
            return shuttle.progress
        if mode not in {"IN_SERVICE", "DETOURING", "RETURNING_TO_DEPOT"}:
            shuttle.curr_vel = 0
            return shuttle.progress
        set_down = self.set_downs.get(shuttle.ID) if mode == "RETURNING_TO_DEPOT" else None
        if set_down is not None and shuttle.progress >= set_down["at_m"]-EPSILON:
            # At the stop where its riders get off: one dwell, then on to the depot empty
            shuttle.curr_vel = 0
            set_down["timer"] = max(0, set_down["timer"]-self.STEP_S)
            if set_down["timer"] < EPSILON:
                del self.set_downs[shuttle.ID]
                self._event(shuttle, "PASSENGERS_SET_DOWN", stop_id=set_down["stop_id"])
            return shuttle.progress
        leaving = mode == "RETURNING_TO_DEPOT" and shuttle.ID in self.parking_chargers
        if leaving and shuttle.progress <= EPSILON and not self._may_enter_yard(shuttle, leaving=True):
            shuttle.curr_vel = 0  # stays in the bay until the shuttle driving in has stopped
            return shuttle.progress
        speed_factor = 1.0
        if shuttle.ID == 0:
            for selection in self.random_incidents:
                if selection.get("requires_guidance") or not selection.get("impact_edge_ids"):
                    continue
                start = selection["trigger_tick"]
                if not start <= self.current_tick < start + selection["clear_after_ticks"]:
                    continue
                current_route = next((r for r in self.domain.routes if r.route_id == self.domain.vehicles[0].route_id), None)
                if current_route:
                    travelled = 0.0
                    for edge_id, path in zip(current_route.directed_edge_ids, current_route.edge_paths_m):
                        travelled += sum(math.hypot(b.x_m-a.x_m, b.y_m-a.y_m) for a, b in zip(path, path[1:]))
                        if travelled > shuttle.progress + EPSILON:
                            if edge_id in selection["impact_edge_ids"]:
                                speed_factor = min(speed_factor, selection.get("speed_factor", 0.5))
                            break
        if mode != "RETURNING_TO_DEPOT" and not shuttle.route_ID.startswith("depot-path-") and shuttle.ID not in self.loop_started:
            self.loop_started[shuttle.ID] = (self.current_tick, shuttle.battery*self.BATTERY_CAPACITY_KWH/100)
        end = min(self._routes[shuttle.route_ID]["total_length"], shuttle.progress+shuttle.cruise_vel*speed_factor*self.STEP_S)
        ahead = [stop for stop in self.stop_progress.get(shuttle.route_ID, []) if stop > shuttle.progress+EPSILON]
        if set_down is not None:
            ahead.append(set_down["at_m"])
        if mode == "RETURNING_TO_DEPOT" and not leaving and shuttle.route_ID.startswith("depot-path-"):
            wait_at = self._yard_entry_m(shuttle)
            if wait_at is not None and shuttle.progress <= wait_at+EPSILON and end > wait_at and not self._may_enter_yard(shuttle):
                ahead.append(wait_at)
        hold = self._signal_hold(shuttle)
        if self.traffic is not None:
            return self._drive_in_traffic(shuttle, shuttle.cruise_vel*speed_factor, ahead, hold)
        if hold is not None:
            ahead.append(hold)
        return min([end, *ahead])

    def _drive_in_traffic(self, shuttle, cruise, stops, light):
        """Where a shuttle gets to this step as a road vehicle in the traffic.

        It brakes evenly to stand exactly at the next of its stops (stops, as progress along its route,
        and the route's end) and at a light that holds it (light), slows for bends, follows or gives way
        to what is in its way, and pulls away no faster than the bus can. It never goes further than
        would touch a vehicle or another shuttle where they stand now.
        """
        route = self._routes[shuttle.route_ID]
        record = self._traffic_shuttle(shuttle)
        path = self.traffic.shuttle_route(shuttle.route_ID, route["waypoints"])
        plan = self.traffic.plan_shuttle(record, path, shuttle.progress, cruise)
        progress = shuttle.progress
        stop_at = min([route["total_length"], *stops])
        limit = min(cruise, plan.bend_mps, math.sqrt(2*COMFORT_BRAKE*max(0.0, stop_at-progress)))
        # What the traffic and the lights allow, as against its own stops and the road
        rules = plan.limit_mps
        if light is not None:
            rules = min(rules, math.sqrt(2*COMFORT_BRAKE*max(0.0, light-progress)))
            stop_at = min(stop_at, light)
        target = min(limit, rules)
        speed = move_towards(record.speed, target, (SHUTTLE_ACCEL if target > record.speed else HARD_BRAKE)*self.STEP_S)
        end = max(progress, min(stop_at, progress+speed*self.STEP_S, progress+plan.cap_m))
        # Whatever the rules allow, a step that would put its body into another is cut short
        held = plan.held
        if end > progress+EPSILON and self.traffic.body_blocker(record, path, end) is not None:
            held = True
            end = next((progress+(end-progress)*share for share in (0.5, 0.2)
                        if self.traffic.body_blocker(record, path, progress+(end-progress)*share) is None), progress)
        # A light far up the road is not what holds it up now
        near_light = light is not None and light-progress <= PLAN_AHEAD_M
        self.traffic_holds[shuttle.ID] = (held, near_light, rules <= limit and rules < cruise)
        return end

    def _traffic_shuttle(self, shuttle):
        """The traffic's record of a shuttle, by the shuttle's public id."""
        return self.traffic.shuttle(self.domain.vehicles[shuttle.ID].vehicle_id, SHUTTLE_FRONT_M, SHUTTLE_HALF_WIDTH_M)

    def _drive_traffic(self):
        """A step of the background traffic every TRAFFIC_STEP_TICKS ticks, before the shuttles move, so
        they see where it has got to. The traffic stands still between its steps, so a shuttle's own
        check that it never drives into a vehicle is exact on every tick."""
        if self.current_tick % TRAFFIC_STEP_TICKS:
            return
        if self.current_tick % HAZARD_REFRESH_TICKS == 0:
            self.traffic.set_hazards(incident_hazards.live_hazards(
                self.random_incidents, self.domain, self.definition.network["edges"], self.STEP_S, self.current_tick))
        self.traffic.step(self.STEP_S*TRAFFIC_STEP_TICKS, self.timestamp_second)

    def _place_in_traffic(self, moved, blocked=()):
        """Tells the traffic where every shuttle is now and how fast it went (moved, metres this step).

        A shuttle held by the traffic, a light or another shuttle is in the traffic, so vehicles queue
        behind it; one standing at its stop or in a bay is passed.
        """
        for shuttle in self.shuttles:
            held_by_traffic, held_by_light, in_traffic = self.traffic_holds.get(shuttle.ID, (False, False, False))
            route = self._routes[shuttle.route_ID]
            self.traffic.place_shuttle(self._traffic_shuttle(shuttle),
                                       self.traffic.shuttle_route(shuttle.route_ID, route["waypoints"]),
                                       shuttle.progress, moved.get(shuttle.ID, 0.0)/self.STEP_S, self.STEP_S,
                                       held_by_traffic=held_by_traffic, held_by_light=held_by_light,
                                       in_traffic=in_traffic or shuttle.ID in blocked)
        self.traffic_holds = {}

    def traffic_frame(self):
        """Where the background traffic and the shuttles' bodies are now, for the 3D view to draw, or None
        when this session has no traffic. Presentation only: it is not part of the public snapshot.

        The vehicles are packed (city_traffic.FRAME_RECORD) and base64-encoded: a frame of the whole
        sample is read by the 3D view ten times a second, and thousands of JSON numbers made it hitch.
        """
        if self.traffic is None:
            return None
        vehicles, count, shuttles = self.traffic.frame()
        return {"tick": self.current_tick, "simulation_time_s": round(self.timestamp_second, 3),
                "vehicle_count": count, "vehicles": base64.b64encode(vehicles).decode("ascii"), "shuttles": shuttles}

    def traffic_frame_due(self) -> bool:
        """True on a tick the background traffic stepped. Its frame then shows the vehicles and the shuttles
        at the same moment, the moment it is stamped with, so the 3D view can draw them between frames
        without one running ahead of the other. Frames come every TRAFFIC_STEP_TICKS ticks, whichever
        ticks publish a snapshot."""
        return self.traffic is not None and self.current_tick % TRAFFIC_STEP_TICKS == 0

    def _signal_hold(self, shuttle):
        """How far the shuttle may go before a red light (or a yellow it can stop for) holds it.

        The lights are the simulated ones the 3D view draws, on the session's clock, simulation time,
        so this is deterministic and a restored session holds where the original did.
        """
        lines = self._route_stop_lines(shuttle.route_ID)
        if not lines:
            return None
        return route_signals.hold_point(lines, self.timestamp_second, shuttle.progress, SHUTTLE_FRONT_M, shuttle.curr_vel)

    def _route_stop_lines(self, route_id):
        """The stop lines along a route, worked out again only when the route's path changes."""
        waypoints = self._routes[route_id]["waypoints"]
        cache = self.__dict__.setdefault("_stop_lines_cache", {})
        known = cache.get(route_id)
        if known is None or known[0] is not waypoints:
            cache[route_id] = known = (waypoints, self._stop_lines_on([(p.x, p.y) for p in waypoints]))
        return known[1]

    @staticmethod
    def _stop_lines_on(points):
        junctions = route_signals.junctions(SIGNAL_AREA)
        return route_signals.stop_lines(points, junctions) if junctions else []

    def _lines_on(self, points):
        return len(self._stop_lines_on(points))

    def _signal_wait_s(self, shuttle, depot):
        """The longest the lights could hold a shuttle on its way in to a depot.

        Every stop line it has still to cross could be red: those left on its route, then those on
        the depot's road in from the route's end (the longest way to any bay, as _route_tail).
        The safety return does not allow for it: standing that long uses a fraction of a kWh, which
        the reserve takes, so a shuttle may arrive that much under its reserve.
        """
        if shuttle.route_ID.startswith("depot-path-"):
            return max(self._lines_on(self._depot_points(shuttle, charger_id)) for charger_id in depot["bays"])*SIGNAL_WAIT_S
        ahead = sum(1 for line in self._route_stop_lines(shuttle.route_ID)
                    if line.progress_m - SHUTTLE_FRONT_M - route_signals.STOP_MARGIN_M >= shuttle.progress - EPSILON)
        end = self._routes[shuttle.route_ID]["waypoints"][-1]
        route_id = self.domain.vehicles[shuttle.ID].route_id
        root = route_id if route_id in depot["connectors"] else self.definition.data["route_edit"]["route_id"]
        key = (end.x, end.y, root, depot["depot_id"])
        tails = self.__dict__.setdefault("_tail_lines", {})
        if key not in tails:
            counts = []
            for charger_id in depot["bays"]:
                points = [(end.x, end.y), *(tuple(p) for p in depot["connectors"][root])]
                bay = self.definition.bay_positions[charger_id]
                points.extend([(bay[0], points[-1][1]), tuple(bay)])
                counts.append(self._lines_on(points))
            tails[key] = max(counts)
        return (ahead + tails[key])*SIGNAL_WAIT_S

    def _schedule_first_incident(self):
        """Only the first shuttle, on its already approved live detour."""
        if self.current_tick < self.incident_due_tick:
            return
        # Network incidents can coexist. Avoid a second *guidance* incident
        # on Shuttle 01 while it is already handling a direct obstruction.
        active_guidance = any(i.vehicle_id == self.domain.vehicles[0].vehicle_id and i.candidate_ids and
                              i.state not in {"RESOLVED", "UNRESOLVED"}
                              for i in self.domain.incidents)
        shuttle = self.shuttles[0]
        vehicle = self.domain.vehicles[0]
        route = next((r for r in self.domain.routes if r.route_id == vehicle.route_id), None)
        if self.modes[shuttle.ID] not in {"IN_SERVICE", "DETOURING"} or route is None:
            return
        if not route.directed_edge_ids or len(route.directed_edge_ids) != len(route.edge_paths_m):
            logger.debug("[INCIDENT DEBUG] no usable directed edge geometry for approved route")
            return
        blocked = {edge for d in self.domain.disruptions
                   if d.active_from_tick <= self.current_tick
                   and (d.active_until_tick is None or self.current_tick < d.active_until_tick)
                   for edge in d.affected_edge_ids}
        remaining, upcoming, walked = [], [], 0.0
        for edge_id, path in zip(route.directed_edge_ids, route.edge_paths_m):
            start_distance = walked
            walked += sum(math.hypot(b.x_m-a.x_m, b.y_m-a.y_m)
                          for a, b in zip(path, path[1:]))
            if walked > shuttle.progress + EPSILON and edge_id not in blocked:
                remaining.append(edge_id)
                # Never close the occupied road. Leave enough distance for the
                # next publication boundary to hold the shuttle before the closure.
                if start_distance > shuttle.progress + 2*shuttle.cruise_vel*self.STEP_S + EPSILON:
                    upcoming.append(edge_id)
        # Real graph edges only. Peer route hash IDs are not road graph edges.
        network_edges = sorted(set(self.definition.data["edges"]) - blocked)
        if not network_edges:
            logger.debug("[INCIDENT DEBUG] no available network edges")
            return
        # Settled incidents leave random_incidents, so an empty list is not "first".
        first_incident = self.random_incident is None
        if first_incident:
            # Deterministic TEST requirement: road closure ahead on the approved route.
            # Do not use a permanently closed edge or a segment already passed.
            eligible = [candidate for candidate in upcoming if candidate in network_edges]
            if not eligible:
                logger.debug("[INCIDENT DEBUG] first incident delayed: no usable upcoming edge")
                return
            edge = eligible[0]
            # Prefer a closure with a usable live detour. A road reachable only
            # through the occupied segment may have no way round from here.
            routes = self.definition.routes
            if vehicle.route_id != routes._fixture["route_id"]:
                routes = RouteEditService(self.definition,
                    fixture={**routes._fixture, "route_id": vehicle.route_id})
            for candidate in eligible:
                hypothetical = self.generate_snapshot()
                hypothetical.disruptions.append(Disruption(
                    disruption_id="incident-selection-preview", aggregate_revision=1,
                    affected_edge_ids=[candidate], active_from_tick=self.current_tick,
                    active_until_tick=self.current_tick+1, reason="Check upcoming incident detour",
                    provenance=hypothetical.source,
                ))
                if routes.continuation_preview(hypothetical, vehicle.vehicle_id).approvable:
                    edge = candidate
                    break
            logger.debug("[INCIDENT DEBUG] TYPE_SELECTED first=True kind=ROAD_BLOCK")
            template = next((t for t in self.incident_catalog.templates if t.kind == "ROAD_BLOCK"), None)
            if template is None:
                logger.debug("[INCIDENT DEBUG] first incident delayed: ROAD_BLOCK template missing")
                return
        else:
            # Weighted type selection, using the session's checkpointed RNG.
            # Keep the first incident a route-blocking ROAD_BLOCK for testing.
            policy = self.incident_catalog.policy
            accident_probability = max(0.0, min(1.0, float(policy.get("road_accident_probability", 0.5))))
            chosen_kind = "ROAD_ACCIDENT" if self.rng.random() < accident_probability else "ROAD_BLOCK"
            matching = [item for item in self.incident_catalog.templates if item.kind == chosen_kind]
            if not matching:
                logger.debug("[INCIDENT DEBUG] no template for %s", chosen_kind)
                return
            template = self.rng.choice(matching)
            logger.debug("[INCIDENT DEBUG] TYPE_SELECTED first=False kind=%s accident_probability=%.2f",
                         template.kind, accident_probability)
            # Explicit impact policy, not a uniformly random network edge.
            # 65% of incidents are intentionally relevant to Shuttle 01;
            # the remainder are intentionally off its remaining route and
            # off its one-hop congestion neighborhood.
            policy = self.incident_catalog.policy
            impact_probability = float(policy.get("shuttle_impact_probability", 0.65))
            impact_probability = max(0.0, min(1.0, impact_probability))
            adjacency = self.definition.data["edges"]
            route_neighbors = set(remaining)
            for route_edge in remaining:
                if route_edge not in adjacency:
                    continue
                u0, v0 = adjacency[route_edge]
                route_neighbors.update(candidate for candidate, (a, b) in adjacency.items()
                                       if a in (u0, v0) or b in (u0, v0))
            direct_candidates = [e for e in upcoming if e in network_edges]
            indirect_candidates = [e for e in network_edges if e in route_neighbors and e not in remaining]
            unrelated_candidates = [e for e in network_edges if e not in route_neighbors]
            wants_impact = self.rng.random() < impact_probability
            # Never create a second direct hold while guidance is pending.
            if wants_impact and template.kind == "ROAD_BLOCK" and not active_guidance and direct_candidates:
                pool = direct_candidates
            elif wants_impact and indirect_candidates:
                pool = indirect_candidates
            elif wants_impact and direct_candidates and not active_guidance:
                pool = direct_candidates
            elif unrelated_candidates:
                pool = unrelated_candidates
            elif indirect_candidates:
                pool = indirect_candidates
            else:
                logger.debug("[INCIDENT DEBUG] no eligible road for a new incident")
                return
            edge = self.rng.choice(pool)
            logger.debug("[INCIDENT DEBUG] IMPACT_POLICY target=%s probability=%.2f pool=%s chosen=%s active_guidance=%s",
                         wants_impact, impact_probability, len(pool), edge, active_guidance)
        adjacency = self.definition.data["edges"]
        u, v = adjacency[edge]
        neighbors = {candidate for candidate, (a, b) in adjacency.items()
                     if a in (u, v) or b in (u, v)}
        # Congestion propagates one network hop; not an arbitrary spatial radius.
        impacted = [candidate for candidate in remaining if candidate == edge or candidate in neighbors]
        direct = edge in remaining
        is_closure = template.kind == "ROAD_BLOCK"
        # Only a closure on the remaining route requires a guidance decision.
        requires_guidance = is_closure and direct and not active_guidance
        # World coordinates are the same planar metres as the route scene.
        start_xy = self.definition.data["nodes"][str(u)]
        end_xy = self.definition.data["nodes"][str(v)]
        selection = {
            "map_marker": {
                "edge_id": edge,
                "start": {"x_m": float(start_xy[0]), "y_m": float(start_xy[1])},
                "end": {"x_m": float(end_xy[0]), "y_m": float(end_xy[1])},
                "center": {"x_m": (float(start_xy[0]) + float(end_xy[0])) / 2,
                           "y_m": (float(start_xy[1]) + float(end_xy[1])) / 2},
            },
            "incident_id": f"random-{template.kind.lower()}-{self.current_tick}",
            "disruption_id": f"random-disruption-{self.current_tick}",
            "kind": template.kind,
            "vehicle_id": vehicle.vehicle_id,
            "route_id": vehicle.route_id,
            "affected_edge_ids": [edge],
            "impact_edge_ids": impacted,
            "requires_guidance": requires_guidance,
            "speed_factor": float(self.incident_catalog.policy.get("congestion_speed_factor", 0.5)) if impacted and not requires_guidance else 1.0,
            "block_current_route_entrance": False,
            "assumptions": [
                "Synthetic training incident from predefined_scenarios.json.",
                f"Type: {template.kind}; incident edge: {edge}; affected route edges: {impacted}.",
                f"Operational impact: {'route directly blocked' if requires_guidance else 'traffic delay possible' if impacted else 'none; no action required'}.",
                "Traffic impact estimated from shared endpoints (one graph hop).",
                TRAFFIC_ASSUMPTION if self.traffic is not None else
                "No traffic volume or lanes are modelled; shuttles stop at simulated traffic lights with made-up timings.",
                "Nearby incidents may reduce speed on connected route edges; direct closures require guidance.",
                "Scoring is provisional and not calibrated for real-world safety.",
            ],
            "trigger_tick": self.current_tick,
            "clear_after_ticks": max(1, round((300.0 if first_incident else template.duration_s)/self.STEP_S)),
        }
        self.random_incident = selection
        self.random_incidents.append(selection)
        self.incident_due_tick = self.current_tick + self.incident_catalog.next_interval_ticks(self.rng, self.STEP_S)
        logger.debug("[INCIDENT DEBUG] SELECTED first=%s id=%s edge=%s impact=%s guidance=%s next_due=%s marker=%s",
                     first_incident, selection["incident_id"], edge, impacted, requires_guidance,
                     self.incident_due_tick, selection["map_marker"])

    def tick(self):
        self.command_tick += 1
        if self.is_paused:
            return
        self.current_tick += 1
        self.timestamp_second = self.current_tick*self.STEP_S
        self.yard_claims = {}
        self._schedule_first_incident()
        # Older checkpoints released a port while leaving its finished shuttle
        # in the bay. Clear that shuttle physically on resume, without releasing
        # a reservation that may now belong to another vehicle.
        for shuttle in self.shuttles:
            if self.modes[shuttle.ID] == "STANDBY" and shuttle.route_ID.startswith("depot-path-"):
                for charger_id, bay in self.definition.bay_positions.items():
                    if math.dist((shuttle.position.x, shuttle.position.y), bay) < EPSILON:
                        self._begin_parking(shuttle, charger_id, release_port=False)
                        self._event(shuttle, "DEPOT_BAY_CLEARANCE_STARTED", charger_id=charger_id)
                        break
        if self.traffic is not None:
            self._drive_traffic()
        charging = {s.ID for s in self.shuttles if self.modes[s.ID] == "CHARGING"}
        proposed = {s.ID: self._prepare_motion(s) for s in self.shuttles}
        proposed, blocked = self._resolve_motion(proposed)
        moved = {}
        for shuttle in self.shuttles:
            mode = self.modes[shuttle.ID]
            distance = moved[shuttle.ID] = abs(proposed[shuttle.ID]-shuttle.progress)
            shuttle.progress = proposed[shuttle.ID]
            if shuttle.ID in blocked and mode in {"IN_SERVICE", "DETOURING", "RETURNING_TO_DEPOT"}:
                shuttle.state, shuttle.curr_vel = ShuttleState.BLOCKED, 0.0
            elif mode in {"IN_SERVICE", "DETOURING", "RETURNING_TO_DEPOT"}:
                shuttle.state, shuttle.curr_vel = ShuttleState.ROUTE_LOOP, distance/self.STEP_S
            level = shuttle.battery*self.BATTERY_CAPACITY_KWH/100
            if shuttle.ID in charging:
                port = next(c for c in self.domain.chargers if self.domain.vehicles[shuttle.ID].vehicle_id in c.occupied_vehicle_ids)
                level = charge_battery_kwh(ENERGY_R1, level, self.BATTERY_CAPACITY_KWH,
                                           port.power_kw*(self.definition.data.get("charge_time_scale") or 1.0), self.STEP_S)
                self.charge_elapsed[shuttle.ID] += self.STEP_S
            level, _ = use_energy(ENERGY_R1, level, distance, self.STEP_S)
            shuttle.battery = level*100/self.BATTERY_CAPACITY_KWH
            shuttle.position = self._route_evaluation(shuttle.route_ID, shuttle.progress)
            if mode == "RETURNING_TO_DEPOT" and shuttle.progress >= self._routes[shuttle.route_ID]["total_length"]-EPSILON:
                shuttle.curr_vel, shuttle.state = 0, ShuttleState.DEPOT_STANDBY
                if shuttle.ID in self.parking_chargers:
                    charger_id = self.parking_chargers.pop(shuttle.ID)
                    if charger_id is not None:
                        port = next(c for c in self.domain.chargers if c.charger_id == charger_id)
                        port.occupied_vehicle_ids.remove(self.domain.vehicles[shuttle.ID].vehicle_id)
                        port.aggregate_revision += 1
                    self._set_mode(shuttle, "STANDBY")
                    self._event(shuttle, "DEPOT_STANDBY_REACHED", charger_id=charger_id)
                    if charger_id is not None and self._back_to_work():
                        # The port is free now; the booking lasts until the shuttle is back on its route
                        self._head_back_to_route(shuttle, charger_id)
                    else:
                        finish_hold(self.domain, self.domain.vehicles[shuttle.ID].vehicle_id)
                else:
                    self._set_mode(shuttle, "QUEUED_FOR_CHARGE")
                    self._event(shuttle, "DEPOT_BAY_ARRIVED")
            elif mode == "QUEUED_FOR_CHARGE":
                self._set_mode(shuttle, "CHARGING")
                shuttle.state = ShuttleState.CHARGING
                self.charge_elapsed[shuttle.ID], self.charge_started_kwh[shuttle.ID] = 0.0, level
                self._event(shuttle, "CHARGING_STARTED")
            elif shuttle.ID in charging and self._charge_finished(shuttle.ID, level):
                # Retain the reservation until the finished shuttle has driven
                # clear of the bay; a public free port must be physically usable.
                self._begin_parking(shuttle, port.charger_id)
                self._event(shuttle, "CHARGING_FINISHED", charger_id=port.charger_id,
                    charged_s=self.charge_elapsed[shuttle.ID], energy_added_kwh=level-self.charge_started_kwh[shuttle.ID], battery_kwh=level)
            elif mode in {"IN_SERVICE", "DETOURING"} and shuttle.route_ID.startswith("depot-path-"):
                if shuttle.progress >= self._routes[shuttle.route_ID]["total_length"]-EPSILON:
                    self._rejoin_route(shuttle)
            elif distance > 0 and (shuttle.progress >= self._routes[shuttle.route_ID]["total_length"]-EPSILON
                    or any(abs(shuttle.progress-stop) < EPSILON for stop in self.stop_progress.get(shuttle.route_ID, []))):
                self._set_mode(shuttle, "DWELLING")
                shuttle.dwell_timer, shuttle.curr_vel, shuttle.state = self.DWELL_S, 0, ShuttleState.STOP_DWELL
                self._note_stop(shuttle)
            if level <= 0 and self.modes[shuttle.ID] in {"IN_SERVICE", "DETOURING", "RETURNING_TO_DEPOT"}:
                self._set_mode(shuttle, "OUT_OF_SERVICE")
                shuttle.curr_vel = 0
                # A shuttle stranded on its way in can never use its port, so it frees it for others
                held = self._held_charger(shuttle)
                if held is not None:
                    held.occupied_vehicle_ids.remove(self.domain.vehicles[shuttle.ID].vehicle_id)
                    held.aggregate_revision += 1
                self.parking_chargers.pop(shuttle.ID, None)
                self._miss_bookings(shuttle)
                self._event(shuttle, "BATTERY_EXHAUSTED", released_charger_id=held.charger_id if held else None)
        self._run_bookings()
        self._safety_returns()
        if self.traffic is not None:
            self._place_in_traffic(moved, blocked)

    def workflow_state(self):
        return {"domain": self.domain.model_dump(mode="json"), "modes": {str(k): v for k, v in self.modes.items()},
            "charge_elapsed": {str(k): v for k, v in self.charge_elapsed.items()},
            "charge_started_kwh": {str(k): v for k, v in self.charge_started_kwh.items()},
            "parking_chargers": {str(k): v for k, v in self.parking_chargers.items()},
            "safety_unavailable": sorted(self.safety_unavailable), "continued": sorted(self.continued),
            "set_downs": {str(k): v for k, v in self.set_downs.items()},
            "paths": [{"route_ID": key, "waypoints": [p.model_dump() for p in path["waypoints"]]} for key, path in self._routes.items()],
            "stop_progress": self.stop_progress, "incident_due_tick": self.incident_due_tick,
            "random_incident": self.random_incident, "random_incidents": self.random_incidents,
            "stop_names": self.stop_names,
            "loop_started": {str(k): list(v) for k, v in self.loop_started.items()},
            "pending_events": [e.model_dump(mode="json") for e in self.pending_events],
            "traffic": self.traffic.state() if self.traffic is not None else None}

    def restore_workflow(self, state, snapshot):
        """Restore checkpointed execution; reject disagreement with durable public state."""
        domain = FullSnapshot.model_validate(state["domain"])
        excluded = {"snapshot_id", "event_sequence", "session_epoch", "session"}
        if domain.model_dump(exclude=excluded) != snapshot.model_dump(exclude=excluded):
            raise TrainingStoreError("CORRUPT_CHECKPOINT", "Workflow and public checkpoint disagree")
        # The service routes, an approved edit and a live detour, and at most one depot path a shuttle
        # (with two depots every bay may be in use)
        if len(state["paths"]) > 7 + len(self.shuttles) or state["pending_events"]:
            raise TrainingStoreError("CORRUPT_CHECKPOINT", "Workflow path/event bounds are invalid")
        self._routes = {p["route_ID"]: validate_routes([p], MAX_ROUTE_WAYPOINTS)[0] for p in state["paths"]}
        self.modes = {int(k): v for k, v in state["modes"].items()}
        self.charge_elapsed = {int(k): v for k, v in state["charge_elapsed"].items()}
        self.charge_started_kwh = {int(k): v for k, v in state["charge_started_kwh"].items()}
        self.parking_chargers = {int(k): v for k, v in state.get("parking_chargers", {}).items()}
        self.safety_unavailable = set(state.get("safety_unavailable", []))
        self.continued = set(state.get("continued", []))
        self.set_downs = {int(k): dict(v) for k, v in state.get("set_downs", {}).items()}
        # Checkpoints from before every stop was served hold a single number per route
        self.stop_progress = {route: stops if isinstance(stops, list) else [stops] for route, stops in state["stop_progress"].items()}
        # Checkpoints from before the outcome report name no stops and time no loops
        self.stop_names = state.get("stop_names", {})
        self.loop_started = {int(k): tuple(v) for k, v in state.get("loop_started", {}).items()}
        self.pending_events = []
        self.incident_due_tick = state.get("incident_due_tick", self.incident_due_tick)
        self.random_incident = state.get("random_incident")
        self.random_incidents = state.get("random_incidents", [self.random_incident] if self.random_incident else [])
        # A checkpoint from before the backend drove the traffic has none: its traffic starts from the
        # area's sample now, as a new session's does
        if "traffic" in state:
            try:
                self.traffic = CityTraffic.from_state(state["traffic"]) if state["traffic"] is not None else None
            except (ValueError, KeyError, TypeError, IndexError) as error:
                raise TrainingStoreError("CORRUPT_CHECKPOINT", "Traffic checkpoint is invalid") from error
        self.traffic_holds = {}
        self.domain = snapshot.model_copy(deep=True)
        if set(self.modes) != set(range(5)) or [s.ID for s in self.shuttles] != list(range(5)):
            raise TrainingStoreError("CORRUPT_CHECKPOINT", "Workflow roster is invalid")
        for shuttle_id, charger_id in self.parking_chargers.items():
            port = next((c for c in snapshot.chargers if c.charger_id == charger_id), None)
            if (shuttle_id not in self.modes or self.modes[shuttle_id] != "RETURNING_TO_DEPOT"
                    or (charger_id is not None and (port is None
                        or snapshot.vehicles[shuttle_id].vehicle_id not in port.occupied_vehicle_ids))):
                raise TrainingStoreError("CORRUPT_CHECKPOINT", "Depot parking reservation is invalid")
        for shuttle in self.shuttles:
            expected = self._route_evaluation(shuttle.route_ID, shuttle.progress)
            if shuttle.progress > self._routes[shuttle.route_ID]["total_length"]+EPSILON or math.hypot(expected.x-shuttle.position.x, expected.y-shuttle.position.y) > EPSILON:
                raise TrainingStoreError("CORRUPT_CHECKPOINT", "Workflow position disagrees with its path")
        if any(not math.isfinite(value) or value < 0 for value in self.charge_elapsed.values()):
            raise TrainingStoreError("CORRUPT_CHECKPOINT", "Charging clock is invalid")
        if self.traffic is not None and "traffic" not in state:
            self._place_in_traffic({})
            self.traffic.clear_shuttles()
        elif self.traffic is not None:
            # A checkpoint keeps which geometry each shuttle was on, not the geometry itself
            for shuttle in self.shuttles:
                record = self.traffic.shuttles.get(self.domain.vehicles[shuttle.ID].vehicle_id)
                path = self.traffic.shuttle_route(shuttle.route_ID, self._routes[shuttle.route_ID]["waypoints"])
                if record is not None and record.route_sig == path.sig:
                    record.route = path
