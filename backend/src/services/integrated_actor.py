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
from src.energy.decisions import with_battery_decisions
from src.energy.forecast import reserve_kwh
from src.energy.model import ENERGY_R1, auxiliary_energy_kwh, charge_battery_kwh, drive_energy_kwh, use_energy
from src.persistence.training_store import TrainingStoreError
from src.routing.road_graph import RoadGraphSnapshot
from src.schemas.shuttle_data import ShuttleState, Vector2D
from src.services.asset_loader import MAX_ROUTE_WAYPOINTS, validate_routes
from src.services.incidents import FIXTURE_PATH as INCIDENT_FIXTURE, IncidentService
from src.services.incident_scenarios.simulated_catalog import SimulatedIncidentCatalog
from src.services.route_edit import RouteEditService
from src.services.session_actor import SessionActor

logger = logging.getLogger(__name__)

FIXTURE_PATH = Path(__file__).resolve().parents[2] / "fixtures/m2_integrated.json"
SCENARIO_SELECTOR = "scenario-m2-integrated-r1"
SCENARIO_ID = "m2-integrated"
EPSILON = 1e-8


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
        self.signature = hashlib.sha256(json.dumps({"fixture": self.data, "network": self.network, "incident": incident,
            "energy": asdict(ENERGY_R1), "controller": "integrated-planar-r1"}, sort_keys=True).encode()).hexdigest()
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
    """One BE-M2-02 controller with recoverable route/incident/charging effects."""

    def __init__(self, seed: int, *, session_id: str):
        self.definition = get_workflow_definition()
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
        self.safety_unavailable = set()
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
            power_kw=50, occupied_vehicle_ids=[]) for key in self.definition.bay_positions]
        self.domain.depots = [Depot(depot_id=depot["depot_id"],
            position=Position(x_m=depot["position_m"][0], y_m=depot["position_m"][1], z_m=0),
            charger_ids=list(depot["bays"])) for depot in self.definition.depots]
        self._adopt_route(primary.route_id, primary.directed_edge_ids)
        self.domain = self.generate_snapshot()

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
        path = geometry(f"depot-path-{shuttle.ID}", self._parking_points(shuttle.ID, charger_id))
        self._routes[path["route_ID"]] = path
        shuttle.route_ID, shuttle.progress = path["route_ID"], 0.0
        self._set_mode(shuttle, "RETURNING_TO_DEPOT")
        shuttle.curr_vel, shuttle.state = shuttle.cruise_vel, ShuttleState.ROUTE_LOOP

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
        route_id = self.domain.vehicles[shuttle.ID].route_id
        connectors = self.definition.charger_depot[charger_id]["connectors"]
        root = route_id if route_id in connectors else self.definition.data["route_edit"]["route_id"]
        points.extend(tuple(p) for p in connectors[root])
        bay = self.definition.bay_positions[charger_id]
        points.extend([(bay[0], points[-1][1]), tuple(bay)])
        return [p for index, p in enumerate(points) if index == 0 or p != points[index-1]]

    def _held_charger(self, shuttle):
        vehicle_id = self.domain.vehicles[shuttle.ID].vehicle_id
        return next((c for c in self.domain.chargers if vehicle_id in c.occupied_vehicle_ids), None)

    def _nearest_depot(self, shuttle):
        return min(self.definition.depots,
                   key=lambda depot: math.dist((shuttle.position.x, shuttle.position.y), depot["position_m"]))

    def _depot_distance(self, shuttle, depot):
        # The longest way in to any of its bays, so the estimate holds whichever bay is free
        return max(self._path_distance(self._depot_points(shuttle, charger_id)) for charger_id in depot["bays"])

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
        free = {c.charger_id for c in self.domain.chargers if len(c.occupied_vehicle_ids) < c.port_count}
        ranked = sorted((self._depot_distance(shuttle, depot), index) for index, depot in enumerate(self.definition.depots))
        distance, index = next(((d, i) for d, i in ranked if free & set(self.definition.depots[i]["bays"])), ranked[0])
        return self.definition.depots[index], distance

    def _safety_returns(self):
        """Send a shuttle back on its own when it can only just still reach a charger above its reserve.

        This is the "bingo" point: the last tick it can turn for the depot its decisions point at
        (the nearest with a free charger) and still arrive at or above the training reserve. At a
        loop end it also turns back when another loop would take it past that point. The trainee
        missed the charging decision, so the SAFETY_RETURN event scores it zero. With every charger
        taken it carries on, and SAFETY_RETURN_UNAVAILABLE is logged once; 0 kWh stays the hard floor.
        """
        speed = self.definition.data["speed_mps"]
        reserve = reserve_kwh(ENERGY_R1, self.BATTERY_CAPACITY_KWH)
        # Two ticks of driving, so the check fires before the margin is gone
        buffer = 2*(drive_energy_kwh(ENERGY_R1, speed*self.STEP_S)+auxiliary_energy_kwh(ENERGY_R1, self.STEP_S))
        for shuttle in self.shuttles:
            if (self.modes[shuttle.ID] not in {"IN_SERVICE", "DWELLING", "DETOURING"}
                    or shuttle.route_ID.startswith("depot-path-") or self._held_charger(shuttle) is not None):
                continue
            level = shuttle.battery*self.BATTERY_CAPACITY_KWH/100
            if level <= 0:
                continue
            depot, distance = self.depot_choice(shuttle)
            arrival = level-drive_energy_kwh(ENERGY_R1, distance)-auxiliary_energy_kwh(ENERGY_R1, distance/speed)
            route = self._routes[shuttle.route_ID]
            if self.modes[shuttle.ID] == "DWELLING" and shuttle.progress >= route["total_length"]-EPSILON:
                # Leaving for another loop commits it to the whole loop before the next way in
                loop_s = (route["total_length"]/speed + shuttle.dwell_timer
                          + (len(self.stop_progress.get(shuttle.route_ID, []))+1)*self.DWELL_S)
                arrival = min(arrival, arrival-drive_energy_kwh(ENERGY_R1, route["total_length"])
                              - auxiliary_energy_kwh(ENERGY_R1, loop_s))
            if arrival-reserve > buffer:
                self.safety_unavailable.discard(shuttle.ID)
                continue
            vehicle = self.domain.vehicles[shuttle.ID]
            charger = next((c for c in sorted(self.domain.chargers, key=lambda c: c.charger_id)
                            if c.charger_id in depot["bays"] and len(c.occupied_vehicle_ids) < c.port_count), None)
            if charger is None:
                if shuttle.ID not in self.safety_unavailable:
                    self.safety_unavailable.add(shuttle.ID)
                    self._event(shuttle, "SAFETY_RETURN_UNAVAILABLE", battery_kwh=level, depot_id=depot["depot_id"])
                continue
            self.safety_unavailable.discard(shuttle.ID)
            charger.occupied_vehicle_ids.append(vehicle.vehicle_id)
            charger.aggregate_revision += 1
            points = self._depot_points(shuttle, charger.charger_id)
            if len(points) > 1:
                path = geometry(f"depot-path-{shuttle.ID}", points)
                self._routes[path["route_ID"]] = path
                shuttle.route_ID, shuttle.progress = path["route_ID"], 0.0
            self._set_mode(shuttle, "RETURNING_TO_DEPOT")
            shuttle.state, shuttle.curr_vel = ShuttleState.ROUTE_LOOP, shuttle.cruise_vel
            self._event(shuttle, "SAFETY_RETURN", battery_kwh=level, reserve_kwh=reserve,
                expected_arrival_kwh=level-drive_energy_kwh(ENERGY_R1, self._path_distance(points))
                    - auxiliary_energy_kwh(ENERGY_R1, self._path_distance(points)/speed),
                depot_id=depot["depot_id"], charger_id=charger.charger_id, distance_m=self._path_distance(points),
                assessment={"rubric_revision": RUBRIC_REVISION, "provisional": True, "uncalibrated": True,
                    "scope": "BATTERY_DECISION", "outcome": "MISSED_DECISION",
                    "criteria": {key: 0 for key in RUBRIC}, "maximum_points": RUBRIC, "total": 0,
                    "assumptions": ["No charging decision was made before the last safe point to turn "
                                    "for the depot; the shuttle returned automatically."]})

    def depot_distances(self):
        return {self.domain.vehicles[shuttle.ID].vehicle_id: self.depot_choice(shuttle)[1] for shuttle in self.shuttles}

    def battery_decisions(self, model, snapshot):
        choices = {self.domain.vehicles[shuttle.ID].vehicle_id: self.depot_choice(shuttle) for shuttle in self.shuttles}
        return with_battery_decisions(model, snapshot, self.definition.data["charge_s"],
            depot_distances_m={vehicle_id: distance for vehicle_id, (_, distance) in choices.items()},
            depot_speed_mps=self.definition.data["speed_mps"],
            parking_distances_m={(vehicle.vehicle_id, charger_id): self._path_distance(self._parking_points(shuttle.ID, charger_id))
                for shuttle, vehicle in zip(self.shuttles, snapshot.vehicles)
                for charger_id in self.definition.bay_positions},
            depot_choices={vehicle_id: depot["depot_id"] for vehicle_id, (depot, _) in choices.items()})

    def generate_snapshot(self) -> FullSnapshot:
        result = self.domain.model_copy(deep=True)
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
        return self.battery_decisions(ENERGY_R1, result)

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

    def synchronize(self, snapshot: FullSnapshot):
        """Apply validated domain consequences to a staged controller, before commit."""
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
                points = self._depot_points(shuttle, port.charger_id)
                distance = self._path_distance(points)
                arrival, _ = use_energy(ENERGY_R1, vehicle.battery_kwh, distance,
                                       distance/self.definition.data["speed_mps"])
                if arrival + EPSILON < reserve_kwh(ENERGY_R1, vehicle.battery_capacity_kwh):
                    raise TrainingStoreError("NO_FEASIBLE_ROUTE", "Admitted depot path would breach the battery reserve")
                if len(points) > 1:
                    path = geometry(f"depot-path-{shuttle.ID}", points)
                    self._routes[path["route_ID"]] = path
                    shuttle.route_ID, shuttle.progress = path["route_ID"], 0.0
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
        self.domain = snapshot.model_copy(deep=True)
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
                    self._note_loop_finished(shuttle)
                    if any(i.vehicle_id == self.domain.vehicles[shuttle.ID].vehicle_id and i.state == "EXECUTING" for i in self.domain.incidents):
                        self._event(shuttle, "DETOUR_COMPLETED")
                detouring = any(i.vehicle_id == self.domain.vehicles[shuttle.ID].vehicle_id and i.state == "EXECUTING"
                    for i in self.domain.incidents) and shuttle.progress != 0
                self._set_mode(shuttle, "DETOURING" if detouring else "IN_SERVICE")
                shuttle.state, shuttle.curr_vel = ShuttleState.ROUTE_LOOP, shuttle.cruise_vel
            return shuttle.progress
        if mode not in {"IN_SERVICE", "DETOURING", "RETURNING_TO_DEPOT"}:
            shuttle.curr_vel = 0
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
        if mode != "RETURNING_TO_DEPOT" and shuttle.ID not in self.loop_started:
            self.loop_started[shuttle.ID] = (self.current_tick, shuttle.battery*self.BATTERY_CAPACITY_KWH/100)
        end = min(self._routes[shuttle.route_ID]["total_length"], shuttle.progress+shuttle.cruise_vel*speed_factor*self.STEP_S)
        ahead = [stop for stop in self.stop_progress.get(shuttle.route_ID, []) if stop > shuttle.progress+EPSILON]
        return min([end, *ahead])

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
                "No traffic volume, lanes or signal timings are modelled.",
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
        charging = {s.ID for s in self.shuttles if self.modes[s.ID] == "CHARGING"}
        proposed = {s.ID: self._prepare_motion(s) for s in self.shuttles}
        proposed, blocked = self._resolve_motion(proposed)
        for shuttle in self.shuttles:
            mode = self.modes[shuttle.ID]
            distance = abs(proposed[shuttle.ID]-shuttle.progress)
            shuttle.progress = proposed[shuttle.ID]
            if shuttle.ID in blocked and mode in {"IN_SERVICE", "DETOURING", "RETURNING_TO_DEPOT"}:
                shuttle.state, shuttle.curr_vel = ShuttleState.BLOCKED, 0.0
            elif mode in {"IN_SERVICE", "DETOURING", "RETURNING_TO_DEPOT"}:
                shuttle.state, shuttle.curr_vel = ShuttleState.ROUTE_LOOP, distance/self.STEP_S
            level = shuttle.battery*self.BATTERY_CAPACITY_KWH/100
            if shuttle.ID in charging:
                port = next(c for c in self.domain.chargers if self.domain.vehicles[shuttle.ID].vehicle_id in c.occupied_vehicle_ids)
                level = charge_battery_kwh(ENERGY_R1, level, self.BATTERY_CAPACITY_KWH, port.power_kw, self.STEP_S)
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
                else:
                    self._set_mode(shuttle, "QUEUED_FOR_CHARGE")
                    self._event(shuttle, "DEPOT_BAY_ARRIVED")
            elif mode == "QUEUED_FOR_CHARGE":
                self._set_mode(shuttle, "CHARGING")
                shuttle.state = ShuttleState.CHARGING
                self.charge_elapsed[shuttle.ID], self.charge_started_kwh[shuttle.ID] = 0.0, level
                self._event(shuttle, "CHARGING_STARTED")
            elif shuttle.ID in charging and self.charge_elapsed[shuttle.ID] >= self.definition.data["charge_s"]-EPSILON:
                # Retain the reservation until the finished shuttle has driven
                # clear of the bay; a public free port must be physically usable.
                self._begin_parking(shuttle, port.charger_id)
                self._event(shuttle, "CHARGING_FINISHED", charger_id=port.charger_id,
                    charged_s=self.charge_elapsed[shuttle.ID], energy_added_kwh=level-self.charge_started_kwh[shuttle.ID], battery_kwh=level)
            elif distance > 0 and (shuttle.progress >= self._routes[shuttle.route_ID]["total_length"]-EPSILON
                    or any(abs(shuttle.progress-stop) < EPSILON for stop in self.stop_progress.get(shuttle.route_ID, []))):
                self._set_mode(shuttle, "DWELLING")
                shuttle.dwell_timer, shuttle.curr_vel, shuttle.state = self.DWELL_S, 0, ShuttleState.STOP_DWELL
                self._note_stop(shuttle)
            if level <= 0 and self.modes[shuttle.ID] in {"IN_SERVICE", "DETOURING", "RETURNING_TO_DEPOT"}:
                self._set_mode(shuttle, "OUT_OF_SERVICE")
                shuttle.curr_vel = 0
                self._event(shuttle, "BATTERY_EXHAUSTED")
        self._safety_returns()

    def workflow_state(self):
        return {"domain": self.domain.model_dump(mode="json"), "modes": {str(k): v for k, v in self.modes.items()},
            "charge_elapsed": {str(k): v for k, v in self.charge_elapsed.items()},
            "charge_started_kwh": {str(k): v for k, v in self.charge_started_kwh.items()},
            "parking_chargers": {str(k): v for k, v in self.parking_chargers.items()},
            "safety_unavailable": sorted(self.safety_unavailable),
            "paths": [{"route_ID": key, "waypoints": [p.model_dump() for p in path["waypoints"]]} for key, path in self._routes.items()],
            "stop_progress": self.stop_progress, "incident_due_tick": self.incident_due_tick,
            "random_incident": self.random_incident, "random_incidents": self.random_incidents,
            "stop_names": self.stop_names,
            "loop_started": {str(k): list(v) for k, v in self.loop_started.items()},
            "pending_events": [e.model_dump(mode="json") for e in self.pending_events]}

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
        # Checkpoints from before every stop was served hold a single number per route
        self.stop_progress = {route: stops if isinstance(stops, list) else [stops] for route, stops in state["stop_progress"].items()}
        # Checkpoints from before the outcome report name no stops and time no loops
        self.stop_names = state.get("stop_names", {})
        self.loop_started = {int(k): tuple(v) for k, v in state.get("loop_started", {}).items()}
        self.pending_events = []
        self.incident_due_tick = state.get("incident_due_tick", self.incident_due_tick)
        self.random_incident = state.get("random_incident")
        self.random_incidents = state.get("random_incidents", [self.random_incident] if self.random_incident else [])
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
