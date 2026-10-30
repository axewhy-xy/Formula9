"""Curated M2 road-level detour preview and pre-trip approval.

Preview computation reads an immutable graph and session snapshot. Only approval
hands a staged revision to the durable single-writer command store.
"""

from __future__ import annotations

from collections import OrderedDict
from copy import deepcopy
from functools import lru_cache
import json
from pathlib import Path
from threading import RLock
from uuid import uuid4

import networkx as nx

from contracts.v2.models import DispatcherCommand, Event, FullSnapshot
from contracts.v2.route_edit import (
    RouteApprovalRequest, RouteCheck, RouteEditPreview, RouteEditRequest, RouteMetrics, RouteEditorContext,
)
from src.persistence.training_store import TrainingStore, TrainingStoreError
from src.routing.road_graph import RoadGraphRepository, get_road_graph_repository


FIXTURE_PATH = Path(__file__).resolve().parents[2] / "fixtures" / "m2_route_detour.json"


def edge_id(key: tuple[int | str, int, str]) -> str:
    return f"{key[0]}:{key[1]}:{key[2]}"


class RouteEditService:
    def __init__(self, graph_repository: RoadGraphRepository, fixture_path: Path = FIXTURE_PATH,
                 *, fixture: dict | None = None) -> None:
        self._graph_repository = graph_repository
        self._fixture = deepcopy(fixture) if fixture is not None else json.loads(fixture_path.read_text(encoding="utf-8"))
        self._previews: OrderedDict[str, tuple[RouteEditRequest, RouteEditPreview]] = OrderedDict()
        self._lock = RLock()

    def _curated_paths(self) -> list[list[str]]:
        return [self._fixture["original_directed_edge_ids"], self._fixture["detour_directed_edge_ids"],
                *self._fixture.get("alternative_directed_edge_paths", [])]

    def _edges(self) -> dict[str, tuple[int, int, dict]]:
        graph = self._graph_repository.get().graph
        return {edge_id(key): (int(u), int(v), data)
                for u, v, key, data in graph.edges(keys=True, data=True)}

    def editor_context(self, snapshot: FullSnapshot, route_id: str) -> RouteEditorContext:
        route = next((r for r in snapshot.routes if r.route_id == route_id), None)
        config = self._fixture
        if route is None or route_id != config["route_id"]:
            raise TrainingStoreError("INVALID_REFERENCE", "No curated operating metadata for this route")
        graph = self._graph_repository.get().graph
        edges = self._edges()
        identifiers = sorted({edge for path in self._curated_paths() for edge in path})
        closed = {edge for d in snapshot.disruptions if d.active_from_tick <= snapshot.tick and
                  (d.active_until_tick is None or snapshot.tick < d.active_until_tick)
                  for edge in d.affected_edge_ids}
        # Missing metadata stays unknown in preview checks; the display must not invent it.
        assumptions = ["Simulation estimates; configuration-dependent training rules."]
        for key, label, unit in (("cruise_speed_kph", "Cruise speed cap", "km/h"),
                                 ("stop_dwell_s", "Dwell per served required stop", "s"),
                                 ("travel_kwh_per_km", "Travel energy", "kWh/km"),
                                 ("auxiliary_kw", "Auxiliary load", "kW")):
            assumptions.append(f"{label}: {config.get(key, 'UNKNOWN')} {unit}")
        assumptions.append("Duration uses the lower of cruise cap and graph speed estimate, plus stop dwell; "
                           "energy uses distance × travel energy + duration × auxiliary load.")
        return RouteEditorContext(session_id=snapshot.session_id, session_epoch=snapshot.session_epoch,
            route_id=route_id, route_revision=route.revision, scene_revision=snapshot.scene_revision,
            fixture_id=config["fixture_id"], start_node_id=config["start_node_id"],
            destination_node_id=config["destination_node_id"],
            nodes=[dict(node_id=node, x=graph.nodes[node]["x"], y=graph.nodes[node]["y"],
                        z=graph.nodes[node].get("z", 0.0))
                   for node in config["operating_node_ids"] if node in graph],
            edges=[dict(edge_id=edge, source=edges[edge][0], target=edges[edge][1], closed=edge in closed)
                   for edge in identifiers if edge in edges],
            original_directed_edge_ids=route.directed_edge_ids, assumptions=assumptions)

    def _walk(self, ids: list[str], edges: dict[str, tuple[int, int, dict]]) -> list[int]:
        nodes: list[int] = []
        for identifier in ids:
            if identifier not in edges:
                return []
            u, v, _ = edges[identifier]
            if nodes and nodes[-1] != u:
                return []
            if not nodes:
                nodes.append(u)
            nodes.append(v)
        return nodes

    def _metrics(self, ids: list[str], nodes: list[int], stops: list[str],
                 edges: dict[str, tuple[int, int, dict]]) -> RouteMetrics | None:
        if not nodes:
            return None
        config = self._fixture
        distance = 0.0
        duration = 0.0
        for identifier in ids:
            if identifier not in edges:
                return None
            data = edges[identifier][2]
            length = float(data["length_m"])
            speed = min(float(data.get("speed_estimate_kph") or config["cruise_speed_kph"]),
                        float(config["cruise_speed_kph"]))
            distance += length
            duration += length / (speed / 3.6)
        coverage = {stop: config["required_stops"].get(stop) in nodes for stop in stops}
        duration += sum(coverage.values()) * float(config["stop_dwell_s"])
        duration += float(config.get("loop_end_dwell_s", 0.0))
        energy = distance / 1000 * float(config["travel_kwh_per_km"])
        energy += duration / 3600 * float(config["auxiliary_kw"])
        return RouteMetrics(distance_m=round(distance, 3), duration_s=round(duration, 3),
                            stop_coverage=coverage, energy_kwh=round(energy, 4))

    def _candidate(self, request: RouteEditRequest, edges: dict[str, tuple[int, int, dict]],
                   closed: set[str]) -> list[str]:
        if request.directed_edge_ids is not None:
            return request.directed_edge_ids
        config = self._fixture
        allowed = {edge for path in self._curated_paths() for edge in path}
        network = nx.DiGraph()
        for identifier in sorted(allowed - closed):
            if identifier in edges:
                u, v, data = edges[identifier]
                weight = float(data["length_m"])
                if not network.has_edge(u, v) or weight < network[u][v]["weight"]:
                    network.add_edge(u, v, weight=weight, identifier=identifier)
        points = [config["start_node_id"], *(request.via_node_ids or config.get("default_via_node_ids", [])),
                  config["destination_node_id"]]
        result: list[str] = []
        try:
            for source, target in zip(points, points[1:]):
                segment = nx.shortest_path(network, source, target, weight="weight")
                result.extend(network[u][v]["identifier"] for u, v in zip(segment, segment[1:]))
        except (nx.NetworkXNoPath, nx.NodeNotFound):
            return []
        return result

    def preview(self, snapshot: FullSnapshot, request: RouteEditRequest, *, retain: bool = True) -> RouteEditPreview:
        route = next((r for r in snapshot.routes if r.route_id == request.route_id), None)
        if route is None:
            raise TrainingStoreError("INVALID_REFERENCE", "Route was not found")
        if request.expected_route_revision != route.revision:
            raise TrainingStoreError("STALE_REVISION", "Route revision changed", current_revision=route.revision)
        if request.route_id != self._fixture["route_id"]:
            raise TrainingStoreError("INVALID_REFERENCE", "No curated operating metadata for this route")
        if self._graph_repository.get().routing_mode != "directed":
            raise TrainingStoreError("UNKNOWN_CONSTRAINT", "Directed road metadata is unavailable")
        edges = self._edges()
        closed = {identifier for disruption in snapshot.disruptions
                  if disruption.active_from_tick <= snapshot.tick and
                  (disruption.active_until_tick is None or snapshot.tick < disruption.active_until_tick)
                  for identifier in disruption.affected_edge_ids}
        ids = self._candidate(request, edges, closed)
        nodes = self._walk(ids, edges)
        config = self._fixture
        checks: list[RouteCheck] = []
        def check(name: str, status: str, message: str, feature: str | None = None) -> None:
            checks.append(RouteCheck(check=name, status=status, message=message, feature_id=feature))

        missing = next((identifier for identifier in ids if identifier not in edges), None)
        wrong_way = False
        direction_message = "Directed graph edges were used"
        if missing:
            parts = missing.split(":")
            if len(parts) == 3:
                opposite = "reverse" if parts[2] == "forward" else "forward"
                wrong_way = f"{parts[0]}:{parts[1]}:{opposite}" in edges
                if wrong_way:
                    direction_message = "Segment traversal conflicts with the known one-way direction"
        if (nodes and config["start_node_id"] != config["destination_node_id"]
                and nodes[0] == config["destination_node_id"] and nodes[-1] == config["start_node_id"]):
            wrong_way = True
            direction_message = "Route runs opposite the required service direction"
        if missing:
            check("GEOMETRY", "FAIL", "Unknown directed road segment", missing)
        elif not ids:
            check("GEOMETRY", "FAIL", "No route connects the requested nodes within the curated network")
        elif not nodes or nodes[0] != config["start_node_id"] or nodes[-1] != config["destination_node_id"]:
            check("GEOMETRY", "FAIL", "Segments are discontinuous or do not reach the route endpoints")
        else:
            check("GEOMETRY", "PASS", "Directed segments form a continuous endpoint-to-endpoint path")
        check("DIRECTION", "FAIL" if wrong_way else "PASS" if nodes else "UNKNOWN",
              direction_message if wrong_way or nodes else "Direction cannot be checked without a path", missing)
        closed_hit = next((identifier for identifier in ids if identifier in closed), None)
        check("CLOSURE", "FAIL" if closed_hit else "PASS", "Route uses an active closure" if closed_hit else "Active closure avoided", closed_hit)
        coverage = {stop: config["required_stops"].get(stop) in nodes for stop in route.required_stop_ids}
        missed = next((stop for stop, served in coverage.items() if not served), None)
        stop_positions = [nodes.index(config["required_stops"][stop]) for stop in route.required_stop_ids] if not missed else []
        out_of_order = not missed and stop_positions != sorted(stop_positions)
        check("STOPS", "FAIL" if missed or out_of_order else "PASS",
              "Required stop is missing" if missed else "Required stops are out of order" if out_of_order else "Required stops are served in order",
              missed or (route.required_stop_ids[0] if out_of_order else None))
        outside = next((str(node) for node in nodes if node not in config["operating_node_ids"]), None)
        check("OPERATING_AREA", "FAIL" if outside else "PASS", "Route leaves the configured operating area" if outside else "Route stays inside the configured operating area", outside)
        curated = {edge for path in self._curated_paths() for edge in path}
        uncurated = next((identifier for identifier in ids if identifier not in curated), None)
        fit_known = all(name in config for name in ("road_width_m", "road_height_m", "vehicle_width_m", "vehicle_height_m", "minimum_side_clearance_m")) and not uncurated
        clearance = float(config["road_width_m"]) - float(config["vehicle_width_m"]) if fit_known else 0.0
        fit_fail = fit_known and (clearance < 2 * float(config["minimum_side_clearance_m"]) or float(config["road_height_m"]) < float(config["vehicle_height_m"]))
        check("VEHICLE_FIT", "UNKNOWN" if not fit_known else "FAIL" if fit_fail else "PASS",
              "Vehicle clearance is not curated for this segment" if not fit_known else "Vehicle does not fit" if fit_fail else "Curated vehicle clearance is sufficient", uncurated)
        # Additional authored alternatives opt in to explicit turn coverage.
        turn_paths = self._curated_paths() if "alternative_directed_edge_paths" in config else [config["detour_directed_edge_ids"]]
        known_turns = {(a, b) for path in turn_paths for a, b in zip(path, path[1:])}
        unknown_turn = next((f"{a}>{b}" for a, b in zip(ids, ids[1:]) if (a, b) not in known_turns), None)
        check("TURNS", "UNKNOWN" if unknown_turn else "PASS", "Turn rule is not curated" if unknown_turn else "Curated turns are permitted", unknown_turn)
        original_ids = route.directed_edge_ids
        original_nodes = self._walk(original_ids, edges)
        edited = self._metrics(ids, nodes, route.required_stop_ids, edges)
        original = self._metrics(original_ids, original_nodes, route.required_stop_ids, edges)
        if original is None:
            check("GEOMETRY", "UNKNOWN", "Original route geometry is unavailable for comparison")
        statuses = {item.status for item in checks if item.mandatory}
        validation = "FAIL" if "FAIL" in statuses else "UNKNOWN" if "UNKNOWN" in statuses else "WARN" if "WARN" in statuses else "PASS"
        preview = RouteEditPreview(preview_id=str(uuid4()), preview_revision=request.draft_revision,
                                   session_id=snapshot.session_id, session_epoch=snapshot.session_epoch,
                                   route_id=route.route_id,
                                   route_revision=route.revision, scene_revision=snapshot.scene_revision,
                                   fixture_id=config["fixture_id"],
                                   closure_revisions={d.disruption_id: d.aggregate_revision for d in snapshot.disruptions},
                                   directed_edge_ids=ids, node_ids=nodes,
                                   polyline=[{"x": float(self._graph_repository.get().graph.nodes[node]["x"]),
                                              "y": float(self._graph_repository.get().graph.nodes[node]["y"]),
                                              "z": float(self._graph_repository.get().graph.nodes[node].get("z", 0.0))}
                                             for node in nodes], checks=checks,
                                   validation=validation, approvable=validation in {"PASS", "WARN"},
                                   original=original, edited=edited)
        if retain:
            with self._lock:
                self._previews[preview.preview_id] = (request.model_copy(deep=True), preview.model_copy(deep=True))
                while len(self._previews) > 256:
                    self._previews.popitem(last=False)
        return preview

    def _finish_approval(self, store: TrainingStore, command: DispatcherCommand):
        """Resume an accepted approval from its durable command, even without a preview cache."""
        snapshot = store.snapshot(command.session_id)
        payload = command.payload
        try:
            if snapshot.session.status != "READY":
                raise TrainingStoreError("INVALID_TRANSITION", "Pre-trip approval requires a READY session")
            if snapshot.scene_revision != payload["scene_revision"] or self._fixture["fixture_id"] != payload["fixture_id"]:
                raise TrainingStoreError("STALE_REVISION", "Scene or route fixture revision changed")
            if {d.disruption_id: d.aggregate_revision for d in snapshot.disruptions} != payload["closure_revisions"]:
                raise TrainingStoreError("STALE_REVISION", "Closure revision changed")
            draft = RouteEditRequest(route_id=command.target_id, expected_route_revision=command.expected_revision,
                                     draft_revision=payload["preview_revision"],
                                     directed_edge_ids=payload["directed_edge_ids"])
            refreshed = self.preview(snapshot, draft, retain=False)
            if not refreshed.approvable or refreshed.directed_edge_ids != payload["directed_edge_ids"]:
                raise TrainingStoreError("NO_FEASIBLE_ROUTE", "Route feasibility changed; request a new preview")
            targets = payload["target_vehicle_ids"]
            assigned = {v.vehicle_id for v in snapshot.vehicles
                        if v.route_id == command.target_id and v.route_revision == command.expected_revision}
            if set(targets) != assigned:
                raise TrainingStoreError("INVALID_REFERENCE", "Approval target set no longer matches assigned shuttles")
        except TrainingStoreError as error:
            return store.apply(command.command_id, rejection_code=error.code)

        proposed = snapshot.model_copy(deep=True)
        route = next(r for r in proposed.routes if r.route_id == command.target_id)
        route.revision += 1
        route.directed_edge_ids = payload["directed_edge_ids"]
        route.edge_paths_m = refreshed.edge_paths_m()
        route.distance_m = refreshed.edited.distance_m
        route.estimated_duration_s = refreshed.edited.duration_s
        for vehicle in proposed.vehicles:
            if vehicle.vehicle_id in targets:
                vehicle.route_revision = route.revision
                vehicle.aggregate_revision += 1
        proposed.event_sequence += 1
        event = Event(event_id=str(uuid4()), event_sequence=proposed.event_sequence,
                      tick=proposed.tick, entity_type="ROUTE", entity_id=route.route_id,
                      entity_revision=route.revision, event_type="ROUTE_APPROVED",
                      source=proposed.source,
                      data={"preview_id": payload["preview_id"], "reason": command.reason,
                            "target_vehicle_ids": targets, "directed_edge_ids": payload["directed_edge_ids"],
                            "energy_kwh": refreshed.edited.energy_kwh})
        return store.apply(command.command_id, proposed, (event,))

    def approve(self, store: TrainingStore, actor_id: str, session_id: str,
                request: RouteApprovalRequest):
        try:
            prior, accepted = store.command_record(session_id, actor_id, request.idempotency_key)
        except TrainingStoreError as error:
            if error.code != "INVALID_REFERENCE":
                raise
        else:
            payload = accepted.payload
            if (accepted.command_type != "APPROVE_ROUTE" or
                accepted.contract_version != request.contract_version or
                payload.get("preview_id") != request.preview_id or
                payload.get("preview_revision") != request.preview_revision or
                accepted.reason != request.reason or
                payload.get("target_vehicle_ids") != request.target_vehicle_ids):
                raise TrainingStoreError("IDEMPOTENCY_CONFLICT", "Idempotency key was used with different command content")
            return self._finish_approval(store, accepted) if prior.status == "QUEUED" else prior
        with self._lock:
            retained = self._previews.get(request.preview_id)
        if retained is None:
            raise TrainingStoreError("INVALID_REFERENCE", "Route preview was not found or has expired")
        draft, preview = retained
        if preview.session_id != session_id or preview.preview_revision != request.preview_revision:
            raise TrainingStoreError("STALE_REVISION", "Route preview revision changed")
        if not preview.approvable:
            raise TrainingStoreError("UNKNOWN_CONSTRAINT", "Route preview has failed or unknown mandatory checks")
        targets = request.target_vehicle_ids
        if len(set(targets)) != len(targets):
            raise TrainingStoreError("INVALID_PAYLOAD", "Target shuttle IDs must be unique")
        command = DispatcherCommand(contract_version="2.0", command_id=request.command_id,
                                    idempotency_key=request.idempotency_key, session_id=session_id,
                                    command_type="APPROVE_ROUTE", expected_epoch=preview.session_epoch,
                                    expected_revision=preview.route_revision, target_type="ROUTE",
                                    target_id=preview.route_id, reason=request.reason,
                                    payload={"preview_id": preview.preview_id,
                                             "preview_revision": preview.preview_revision,
                                             "fixture_id": preview.fixture_id,
                                             "scene_revision": preview.scene_revision,
                                             "closure_revisions": preview.closure_revisions,
                                             "directed_edge_ids": preview.directed_edge_ids,
                                             "target_vehicle_ids": targets})
        snapshot = store.snapshot(session_id)
        if snapshot.session.status != "READY":
            raise TrainingStoreError("INVALID_TRANSITION", "Pre-trip approval requires a READY session")
        if snapshot.scene_revision != preview.scene_revision or {d.disruption_id: d.aggregate_revision for d in snapshot.disruptions} != preview.closure_revisions:
            raise TrainingStoreError("STALE_REVISION", "Scene or closure revision changed")
        refreshed = self.preview(snapshot, draft, retain=False)
        if not refreshed.approvable or refreshed.directed_edge_ids != preview.directed_edge_ids:
            raise TrainingStoreError("STALE_REVISION", "Route feasibility changed; request a new preview")
        assigned = {v.vehicle_id for v in snapshot.vehicles if v.route_id == preview.route_id and v.route_revision == preview.route_revision}
        if any(vehicle_id not in assigned for vehicle_id in targets):
            raise TrainingStoreError("INVALID_REFERENCE", "Target shuttle is not assigned to this route revision")
        if set(targets) != assigned:
            raise TrainingStoreError("INVALID_PAYLOAD", "This M2 route pattern requires every assigned shuttle as an explicit target")
        receipt = store.accept(actor_id, command)
        if receipt.status != "QUEUED":
            return receipt
        return self._finish_approval(store, command)


@lru_cache
def get_route_edit_service() -> RouteEditService:
    return RouteEditService(get_road_graph_repository())
