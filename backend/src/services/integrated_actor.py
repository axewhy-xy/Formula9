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
import math
from pathlib import Path
from uuid import uuid4

import networkx as nx

from contracts.v2.models import Charger, Depot, Disruption, Event, FullSnapshot, Position, RouteStop
from src.energy.decisions import with_battery_decisions
from src.energy.forecast import reserve_kwh
from src.energy.model import ENERGY_R1, charge_battery_kwh, use_energy
from src.persistence.training_store import TrainingStoreError
from src.routing.road_graph import RoadGraphSnapshot
from src.schemas.shuttle_data import ShuttleState, Vector2D
from src.services.asset_loader import validate_routes
from src.services.incidents import FIXTURE_PATH as INCIDENT_FIXTURE, IncidentService
from src.services.route_edit import RouteEditService
from src.services.session_actor import SessionActor

FIXTURE_PATH = Path(__file__).resolve().parents[2] / "fixtures/m2_integrated.json"
SCENARIO_SELECTOR = "scenario-m2-integrated-r1"
SCENARIO_ID = "m2-integrated"
EPSILON = 1e-8


def geometry(identifier: str, points: list) -> dict:
    """Validate one bounded path and derive its actual metre length."""
    return validate_routes([{"route_ID": identifier, "waypoints": [{"x": p[0], "y": p[1]} for p in points]}])[0]


class WorkflowDefinition:
    """Shared authored geometry and thread-safe domain services, loaded off-tick."""

    def __init__(self):
        self.data = json.loads(FIXTURE_PATH.read_bytes())
        incident = json.loads(INCIDENT_FIXTURE.read_bytes())
        incident.update(self.data["incident_overrides"])
        incident["assumptions"] = [*incident["assumptions"], *self.data["assumptions"]]
        self.signature = hashlib.sha256(json.dumps({"fixture": self.data, "incident": incident,
            "energy": asdict(ENERGY_R1), "controller": "integrated-planar-r1"}, sort_keys=True).encode()).hexdigest()
        graph = nx.MultiDiGraph()
        for identifier, point in self.data["nodes"].items():
            graph.add_node(int(identifier), x=float(point[0]), y=float(point[1]), z=0.0)
        for identifier, (u, v) in self.data["edges"].items():
            parts = identifier.split(":")
            graph.add_edge(u, v, key=(parts[0], int(parts[1]), parts[2]),
                length_m=math.dist(self.data["nodes"][str(u)], self.data["nodes"][str(v)]),
                speed_estimate_kph=self.data["speed_mps"] * 3.6)
        self.graph = RoadGraphSnapshot(nx.freeze(graph), "directed", ())
        self.routes = RouteEditService(self, fixture=self.data["route_edit"])
        self.incidents = IncidentService(self.routes, fixture=incident)
        self.paths = {}
        self.terminals = {}
        for ids in self.routes._curated_paths():
            nodes = self.routes._walk(ids, self.routes._edges())
            if not nodes:
                raise ValueError("Integrated route geometry is discontinuous")
            path = geometry(self.data["route_edit"]["route_id"], [self.data["nodes"][str(n)] for n in nodes])
            self.paths[tuple(ids)] = path
            stop = nodes.index(self.data["route_edit"]["default_via_node_ids"][0])
            self.terminals[tuple(ids)] = sum(math.dist((a.x, a.y), (b.x, b.y))
                                           for a, b in zip(path["waypoints"][:stop], path["waypoints"][1:stop+1]))
        original = self.paths[tuple(self.data["route_edit"]["original_directed_edge_ids"])]
        self.actor_routes = [original, *self.data["peer_patterns"]]
        # Separate exit aisles avoid opposing bay departures meeting head-on.
        # The entry aisle and parked row stay clear of both exit aisles.
        # Derive the bounded yard geometry from the authored depot in metres.
        spacing = 3 * SessionActor.SEPARATION_M
        bays_y = min(p[1] for p in self.data["bay_positions_m"].values())
        self.parking_exit_aisles = {charger_id: bays_y-(index+1)*2*SessionActor.SEPARATION_M
            for index, charger_id in enumerate(self.data["bay_positions_m"])}
        self.parking_aisle_y = min(self.parking_exit_aisles.values())-2*SessionActor.SEPARATION_M
        count = len(self.data["vehicle_assignments"])
        self.parking_positions = {
            index: (self.data["depot_position_m"][0] + (index-(count-1)/2)*spacing,
                    self.parking_aisle_y - 2 * SessionActor.SEPARATION_M)
            for index in range(count)
        }

    def get(self):
        return self.graph

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
        data = self.definition.data
        pin = hashlib.sha256(f"{self.definition.signature}:{seed}".encode()).hexdigest()[:24]
        self.scenario_revision = f"fleet-scenario-{pin}"
        self.scene_revision = f"fleet-scene-{self.definition.signature[:24]}"
        self.modes = {s.ID: "STANDBY" for s in self.shuttles}
        self.charge_elapsed = {}
        self.charge_started_kwh = {}
        self.parking_chargers = {}
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
        nodes = self.definition.routes._walk(primary.directed_edge_ids, self.definition.routes._edges())
        corners = [Position(x_m=float(data["nodes"][str(n)][0]), y_m=float(data["nodes"][str(n)][1]), z_m=0)
                   for n in nodes]
        primary.edge_paths_m = [[a, b] for a, b in zip(corners, corners[1:])]
        primary.required_stop_ids = list(data["route_edit"]["required_stops"])
        primary.stops_m = [RouteStop(stop_id=stop, position=Position(x_m=float(data["nodes"][str(node)][0]),
                                                                     y_m=float(data["nodes"][str(node)][1]), z_m=0))
                           for stop, node in data["route_edit"]["required_stops"].items()]
        for route in self.domain.routes:
            route.estimated_duration_s = route.distance_m / data["speed_mps"] + self.DWELL_S
        primary.estimated_duration_s += data["route_edit"]["loop_end_dwell_s"]
        self.domain.disruptions = [Disruption(disruption_id="closure-m2", aggregate_revision=1,
            affected_edge_ids=data["route_edit"]["closed_directed_edge_ids"], active_from_tick=0,
            reason="Authored permanent main-road closure; approve a pre-trip bypass", provenance=self.domain.source)]
        self.domain.chargers = [Charger(charger_id=key, aggregate_revision=1, port_count=1,
            power_kw=50, occupied_vehicle_ids=[]) for key in data["bay_positions_m"]]
        self.domain.depots = [Depot(depot_id=data["depot_id"],
            position=Position(x_m=data["depot_position_m"][0], y_m=data["depot_position_m"][1], z_m=0),
            charger_ids=list(data["bay_positions_m"]))]
        self.stop_progress[primary.route_id] = self.definition.terminals[tuple(primary.directed_edge_ids)]
        self.domain = self.generate_snapshot()

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

    def _yard_points(self, start, destination, *, aisle_y=None):
        if math.dist(start, destination) < EPSILON:
            return [tuple(start)]
        aisle_y = self.definition.parking_aisle_y if aisle_y is None else aisle_y
        points = [tuple(start), (start[0], aisle_y), (destination[0], aisle_y), tuple(destination)]
        return [p for index, p in enumerate(points) if index == 0 or p != points[index-1]]

    def _parking_points(self, shuttle_id, charger_id):
        return self._yard_points(self.definition.data["bay_positions_m"][charger_id],
                                 self.definition.parking_positions[shuttle_id],
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
            return self._yard_points((shuttle.position.x, shuttle.position.y),
                                      self.definition.data["bay_positions_m"][charger_id])
        path = self._routes[shuttle.route_ID]
        points = [(shuttle.position.x, shuttle.position.y)]
        travelled = 0.0
        for a, b in zip(path["waypoints"], path["waypoints"][1:]):
            travelled += math.hypot(b.x-a.x, b.y-a.y)
            if travelled > shuttle.progress + EPSILON:
                points.append((b.x, b.y))
        route_id = self.domain.vehicles[shuttle.ID].route_id
        root = route_id if route_id in self.definition.data["depot_connectors"] else self.definition.data["route_edit"]["route_id"]
        points.extend(tuple(p) for p in self.definition.data["depot_connectors"][root])
        bay = self.definition.data["bay_positions_m"][charger_id]
        points.extend([(bay[0], points[-1][1]), tuple(bay)])
        return [p for index, p in enumerate(points) if index == 0 or p != points[index-1]]

    def depot_distances(self):
        distances = {}
        for shuttle in self.shuttles:
            vehicle_id = self.domain.vehicles[shuttle.ID].vehicle_id
            if self.modes[shuttle.ID] == "RETURNING_TO_DEPOT":
                distances[vehicle_id] = max(0.0, self._routes[shuttle.route_ID]["total_length"]-shuttle.progress)
            elif self.modes[shuttle.ID] in {"QUEUED_FOR_CHARGE", "CHARGING"}:
                distances[vehicle_id] = 0.0
            else:
                distances[vehicle_id] = max(self._path_distance(points)
                    for points in (self._depot_points(shuttle, key) for key in self.definition.data["bay_positions_m"]))
        return distances

    def battery_decisions(self, model, snapshot):
        return with_battery_decisions(model, snapshot, self.definition.data["charge_s"],
            depot_distances_m=self.depot_distances(), depot_speed_mps=self.definition.data["speed_mps"],
            parking_distances_m={(vehicle.vehicle_id, charger_id): self._path_distance(self._parking_points(shuttle.ID, charger_id))
                for shuttle, vehicle in zip(self.shuttles, snapshot.vehicles)
                for charger_id in self.definition.data["bay_positions_m"]})

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
        for route in snapshot.routes:
            key = tuple(route.directed_edge_ids)
            if key in self.definition.paths:
                self._routes[route.route_id] = self.definition.paths[key]
                self.stop_progress[route.route_id] = self.definition.terminals[key]
        for shuttle, vehicle in zip(self.shuttles, snapshot.vehicles):
            prior = old[vehicle.vehicle_id]
            if (vehicle.route_id, vehicle.route_revision) != (prior.route_id, prior.route_revision):
                if vehicle.route_id not in self._routes:
                    raise TrainingStoreError("UNKNOWN_CONSTRAINT", "Route has no admitted actor path")
                shuttle.progress = self._project(self._routes[vehicle.route_id], shuttle.position)
                shuttle.route_ID = vehicle.route_id
            if starting:
                vehicle.service_state = "DWELLING" if shuttle.ID == 0 else "IN_SERVICE"
                vehicle.aggregate_revision += 1
                shuttle.dwell_timer = self.definition.incidents.config["trigger_tick"]*self.STEP_S if shuttle.ID == 0 else 0
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
            changed = mode != self.modes[shuttle.ID] or starting
            self.modes[shuttle.ID] = mode
            if changed:
                shuttle.state = (ShuttleState.ROUTE_LOOP if mode in {"IN_SERVICE", "DETOURING", "RETURNING_TO_DEPOT"}
                    else ShuttleState.STOP_DWELL if mode == "DWELLING" else ShuttleState.CHARGING if mode == "CHARGING"
                    else ShuttleState.DEPOT_STANDBY)
                shuttle.curr_vel = self.definition.data["speed_mps"] if shuttle.state == ShuttleState.ROUTE_LOOP else 0.0
            shuttle.position = Vector2D(x=vehicle.position.x_m, y=vehicle.position.y_m)
            shuttle.battery = vehicle.battery_kwh*100/vehicle.battery_capacity_kwh
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
        end = min(self._routes[shuttle.route_ID]["total_length"], shuttle.progress+shuttle.cruise_vel*self.STEP_S)
        terminal = self.stop_progress.get(shuttle.route_ID)
        if terminal is not None and shuttle.progress < terminal-EPSILON:
            end = min(end, terminal)
        return end

    def tick(self):
        self.command_tick += 1
        if self.is_paused:
            return
        self.current_tick += 1
        self.timestamp_second = self.current_tick*self.STEP_S
        # Older checkpoints released a port while leaving its finished shuttle
        # in the bay. Clear that shuttle physically on resume, without releasing
        # a reservation that may now belong to another vehicle.
        for shuttle in self.shuttles:
            if self.modes[shuttle.ID] == "STANDBY" and shuttle.route_ID.startswith("depot-path-"):
                for charger_id, bay in self.definition.data["bay_positions_m"].items():
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
                shuttle.state, shuttle.curr_vel = ShuttleState.ROUTE_LOOP, shuttle.cruise_vel
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
                    or abs(shuttle.progress-self.stop_progress.get(shuttle.route_ID, -1)) < EPSILON):
                self._set_mode(shuttle, "DWELLING")
                shuttle.dwell_timer, shuttle.curr_vel, shuttle.state = self.DWELL_S, 0, ShuttleState.STOP_DWELL
            if level <= 0 and self.modes[shuttle.ID] in {"IN_SERVICE", "DETOURING", "RETURNING_TO_DEPOT"}:
                self._set_mode(shuttle, "OUT_OF_SERVICE")
                shuttle.curr_vel = 0
                self._event(shuttle, "BATTERY_EXHAUSTED")

    def workflow_state(self):
        return {"domain": self.domain.model_dump(mode="json"), "modes": {str(k): v for k, v in self.modes.items()},
            "charge_elapsed": {str(k): v for k, v in self.charge_elapsed.items()},
            "charge_started_kwh": {str(k): v for k, v in self.charge_started_kwh.items()},
            "parking_chargers": {str(k): v for k, v in self.parking_chargers.items()},
            "paths": [{"route_ID": key, "waypoints": [p.model_dump() for p in path["waypoints"]]} for key, path in self._routes.items()],
            "stop_progress": self.stop_progress, "pending_events": [e.model_dump(mode="json") for e in self.pending_events]}

    def restore_workflow(self, state, snapshot):
        """Restore checkpointed execution; reject disagreement with durable public state."""
        domain = FullSnapshot.model_validate(state["domain"])
        excluded = {"snapshot_id", "event_sequence", "session_epoch", "session"}
        if domain.model_dump(exclude=excluded) != snapshot.model_dump(exclude=excluded):
            raise TrainingStoreError("CORRUPT_CHECKPOINT", "Workflow and public checkpoint disagree")
        if len(state["paths"]) > 9 or state["pending_events"]:
            raise TrainingStoreError("CORRUPT_CHECKPOINT", "Workflow path/event bounds are invalid")
        self._routes = {p["route_ID"]: validate_routes([p])[0] for p in state["paths"]}
        self.modes = {int(k): v for k, v in state["modes"].items()}
        self.charge_elapsed = {int(k): v for k, v in state["charge_elapsed"].items()}
        self.charge_started_kwh = {int(k): v for k, v in state["charge_started_kwh"].items()}
        self.parking_chargers = {int(k): v for k, v in state.get("parking_chargers", {}).items()}
        self.stop_progress = state["stop_progress"]
        self.pending_events = []
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
