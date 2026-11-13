"""Curated M2 road-level detour preview and pre-trip approval.

Preview computation reads an immutable graph and session snapshot. Only approval
hands a staged revision to the durable single-writer command store.
"""

from __future__ import annotations

from collections import OrderedDict
from copy import copy, deepcopy
from functools import lru_cache
import json
import math
from pathlib import Path
from threading import RLock
from uuid import uuid4

import networkx as nx

from contracts.v2.models import DispatcherCommand, Disruption, Event, FullSnapshot
from contracts.v2.route_edit import (
    RouteApprovalRequest, RouteCheck, RouteEditPreview, RouteEditRequest, RouteMetrics, RouteEditorContext,
    RouteServiceImpact,
)
from src.energy.forecast import reserve_kwh
from src.energy.model import ENERGY_R1
from src.persistence.training_store import TrainingStore, TrainingStoreError
from src.routing.operating_network import OperatingNetwork, capitalised
from src.routing.road_graph import RoadGraphRepository, get_road_graph_repository


FIXTURE_PATH = Path(__file__).resolve().parents[2] / "fixtures" / "m2_route_detour.json"
# The fixture's keys that describe one route. Everything else (the shuttle's size, energy, the road
# network) is the same for every route. A fixture describes its own route at the top level and any
# other route it can edit under "peer_routes", by route ID, with these keys.
ROUTE_KEYS = ("route_id", "start_node_id", "destination_node_id", "default_via_node_ids", "required_stops",
              "stop_approach_edge_ids", "original_directed_edge_ids", "closed_directed_edge_ids",
              "detour_directed_edge_ids", "alternative_directed_edge_paths")


def edge_id(key: tuple[int | str, int, str]) -> str:
    return f"{key[0]}:{key[1]}:{key[2]}"


# World events a detour is taken round by keeping off their scene's road; a closure's road is closed already
SCENE_EVENTS = ("BREAKDOWN", "COLLISION")


def detour_target(snapshot: FullSnapshot, riders: set[str]):
    """The incident and shuttle a detour drafted for a route answers: the latest incident still to be
    decided for that offers one of the route's shuttles a road detour. (None, None) when there is none."""
    for incident in sorted(snapshot.incidents, key=lambda i: i.detected_tick, reverse=True):
        if incident.state != "OPTIONS_READY":
            continue
        for candidate in snapshot.candidates:
            vehicle_id = candidate.vehicle_id or incident.vehicle_id
            if candidate.incident_id == incident.incident_id and candidate.action == "ROAD_DETOUR" and vehicle_id in riders:
                return incident, vehicle_id
    return None, None


def scene_closed(snapshot: FullSnapshot, incident, driven) -> FullSnapshot:
    """The snapshot a detour round a breakdown or a collision is planned and checked in: its scene's road
    closed for the moment, as a closure's already is, unless the shuttle has already driven onto it."""
    location = getattr(incident, "location", None)
    edge = location.edge_id if location is not None and incident.event_type in SCENE_EVENTS else None
    closed = {e for d in snapshot.disruptions if d.active_from_tick <= snapshot.tick
              and (d.active_until_tick is None or snapshot.tick < d.active_until_tick) for e in d.affected_edge_ids}
    if edge is None or edge in closed or edge in set(driven):
        return snapshot
    hypothetical = snapshot.model_copy(deep=True)
    hypothetical.disruptions.append(Disruption(
        disruption_id=f"{incident.incident_id}-scene", aggregate_revision=1, affected_edge_ids=[edge],
        active_from_tick=snapshot.tick, active_until_tick=snapshot.tick + 1,
        reason="Plan a detour round the incident's scene", provenance=snapshot.source))
    return hypothetical


def stop_label(stop_id: str) -> str:
    """A stop's name for people. Scenario stops are named <LTA code>-<slug of its name>."""
    code, _, name = stop_id.partition("-")
    words = (name if code.isdigit() and name else stop_id).replace("-", " ")
    return " ".join(word.upper() if len(word) <= 3 and any(c.isdigit() for c in word) else word.capitalize() for word in words.split())


class RouteEditService:
    def __init__(self, graph_repository: RoadGraphRepository, fixture_path: Path = FIXTURE_PATH,
                 *, fixture: dict | None = None) -> None:
        self._graph_repository = graph_repository
        self._fixture = deepcopy(fixture) if fixture is not None else json.loads(fixture_path.read_text(encoding="utf-8"))
        # Every route's editor data by route ID: this fixture's own route and its peer routes, each with
        # the shared settings
        peers = self._fixture.pop("peer_routes", {})
        shared = {key: value for key, value in self._fixture.items() if key not in ROUTE_KEYS}
        self._configs = {self._fixture["route_id"]: self._fixture,
                         **{route_id: {**shared, **config, "route_id": route_id} for route_id, config in peers.items()}}
        # A detour is traced back to its route by its stops (base_route_id), so no two routes may share them
        by_stops: dict[frozenset, str] = {}
        for route_id, config in self._configs.items():
            stops = frozenset(config["required_stops"])
            if stops in by_stops:
                raise ValueError(f"Routes {by_stops[stops]} and {route_id} serve the same stops, so a detour "
                                 "taken from either could not be traced back to it")
            by_stops[stops] = route_id
        self._previews: OrderedDict[str, tuple[RouteEditRequest, RouteEditPreview]] = OrderedDict()
        self._lock = RLock()
        # Routes with no operating network keep to their curated paths and curated metadata.
        network = self._fixture.get("network")
        self._network = OperatingNetwork(network) if network is not None else None

    def route_config(self, route_id: str) -> dict | None:
        """A route's own editor data (start, destination, stops and original roads, with the shared
        settings), or None for a route the editor has none for. A detour has none of its own."""
        return self._configs.get(route_id)

    def base_route_id(self, snapshot: FullSnapshot, route_id: str | None) -> str | None:
        """The route whose editor data a route is edited with: its own, or for an incident's detour the
        route it was taken from, which is the one whose stops it keeps (no two routes share them; see
        __init__). None when there is none."""
        if route_id in self._configs:
            return route_id
        if not route_id or not any(route_id == i.incident_id + "-detour" or route_id.startswith(i.incident_id + "-detour-")
                                   for i in snapshot.incidents):
            return None
        route = next((r for r in snapshot.routes if r.route_id == route_id), None)
        if route is None:
            return None
        stops = set(route.required_stop_ids)
        return next((base for base, config in self._configs.items() if set(config["required_stops"]) == stops), None)

    def _curated_paths(self) -> list[list[str]]:
        return [self._fixture["original_directed_edge_ids"], self._fixture.get("detour_directed_edge_ids", []),
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
        identifiers = sorted(self._network.edges) if self._network else sorted({edge for path in self._curated_paths() for edge in path})
        closed = self._closed(snapshot)
        # Missing metadata stays unknown in preview checks; the display must not invent it.
        assumptions = ["Simulation estimates; configuration-dependent training rules."]
        for key, label, unit in (("cruise_speed_kph", "Cruise speed cap", "km/h"),
                                 ("stop_dwell_s", "Dwell per served required stop", "s"),
                                 ("travel_kwh_per_km", "Travel energy", "kWh/km"),
                                 ("auxiliary_kw", "Auxiliary load", "kW")):
            assumptions.append(f"{label}: {config.get(key, 'UNKNOWN')} {unit}")
        assumptions.append("Duration uses the lower of cruise cap and graph speed estimate, plus stop dwell; "
                           "energy uses distance × travel energy + duration × auxiliary load.")
        if self._network:
            assumptions.append("Road width, height limits and access come from OpenStreetMap tags where mapped; a lane is "
                               f"otherwise assumed {self._network.rules['default_lane_width_m']} m wide and an open road "
                               f"{self._network.rules['open_road_headroom_m']} m clear. Roads with no basis are unknown and "
                               "block approval. Mapped turn restrictions are not in this extract; only U-turns are refused.")
        return RouteEditorContext(session_id=snapshot.session_id, session_epoch=snapshot.session_epoch,
            route_id=route_id, route_revision=route.revision, scene_revision=snapshot.scene_revision,
            fixture_id=config["fixture_id"], start_node_id=config["start_node_id"],
            destination_node_id=config["destination_node_id"],
            nodes=[dict(node_id=node, x=graph.nodes[node]["x"], y=graph.nodes[node]["y"],
                        z=graph.nodes[node].get("z", 0.0))
                   for node in config["operating_node_ids"] if node in graph],
            edges=[self._editor_edge(edge, edges[edge], closed) for edge in identifiers if edge in edges],
            stops=[dict(stop_id=stop, name=stop_label(stop), node_id=node, x=graph.nodes[node]["x"], y=graph.nodes[node]["y"])
                   for stop, node in config["required_stops"].items() if node in graph and stop in route.required_stop_ids],
            original_directed_edge_ids=route.directed_edge_ids, assumptions=assumptions)

    def _editor_edge(self, identifier: str, edge: tuple[int, int, dict], closed: set[str]) -> dict:
        result = dict(edge_id=identifier, source=edge[0], target=edge[1], closed=identifier in closed)
        if self._network:
            status, note = self._network.fit(identifier, self._fixture)
            result.update(road_name=self._network.road_name(identifier), fit=status, fit_note=note,
                          path=[dict(x=x, y=y, z=0.0) for x, y in self._network.edges[identifier]["path"]])
        return result

    @staticmethod
    def _closed(snapshot: FullSnapshot) -> set[str]:
        return {edge for d in snapshot.disruptions if d.active_from_tick <= snapshot.tick and
                (d.active_until_tick is None or snapshot.tick < d.active_until_tick)
                for edge in d.affected_edge_ids}

    def _stop_visits(self, ids: list[str], nodes: list[int]) -> dict[str, list[int]]:
        """Each required stop's positions in the node path where it is actually served: from its kerb."""
        config = self._fixture
        approaches = config.get("stop_approach_edge_ids", {})
        visits: dict[str, list[int]] = {}
        for stop, node in config["required_stops"].items():
            allowed = approaches.get(stop)
            visits[stop] = [index for index, at in enumerate(nodes) if at == node and
                            (allowed is None or (index > 0 and ids[index - 1] in allowed))]
        return visits

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
        visits = self._stop_visits(ids, nodes)
        coverage = {stop: bool(visits.get(stop)) for stop in stops}
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
        if request.waypoint_edge_ids:
            approaches = config.get("stop_approach_edge_ids", {})
            stops = [approaches[stop] for stop in config["required_stops"] if stop in approaches]
            planned, self._unreached = self._network.plan(config["start_node_id"], config["destination_node_id"], stops,
                                                          request.waypoint_edge_ids, closed, config)
            return planned
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

    def first_avoidable_edge(self, snapshot: FullSnapshot, route_id: str) -> str:
        """The first road of a route with a way round it: blocking the shared entrance would strand the shuttle."""
        route = next(r for r in snapshot.routes if r.route_id == route_id)
        edges, closed = self._edges(), self._closed(snapshot)
        draft = RouteEditRequest(route_id=route_id, expected_route_revision=route.revision, draft_revision=1)
        for identifier in route.directed_edge_ids:
            if self._candidate(draft, edges, closed | {identifier}):
                return identifier
        return route.directed_edge_ids[0]

    def for_route(self, snapshot: FullSnapshot, route_id: str) -> "RouteEditService":
        """The service that edits a route, with its own operating metadata; an incident's detour keeps that
        of the route it replaced. Itself for a route with none, whose previews it then refuses.

        It shares this service's road network and its retained previews, so any of them approves a preview."""
        base = self.base_route_id(snapshot, route_id)
        if base is None:
            return self
        config = self._configs[base]
        if base != route_id:
            config = {**config, "route_id": route_id}
        if config is self._fixture:
            return self
        service = copy(self)
        service._fixture = config
        return service

    def draft_preview(self, snapshot: FullSnapshot, request: RouteEditRequest) -> RouteEditPreview:
        """What an operator's draft does. A shuttle being decided for at an incident carries on from where it
        stands, round the incident's scene."""
        riders = {v.vehicle_id for v in snapshot.vehicles if v.route_id == request.route_id}
        incident, vehicle_id = detour_target(snapshot, riders)
        if incident is None or self._network is None:
            return self.preview(snapshot, request)
        self._check_waypoints(request)
        vehicle = next(v for v in snapshot.vehicles if v.vehicle_id == vehicle_id)
        route = next(r for r in snapshot.routes if r.route_id == vehicle.route_id)
        planning = scene_closed(snapshot, incident, self._driven_prefix(vehicle, route))
        return self.continuation_preview(planning, vehicle_id, request)

    def _driven_prefix(self, vehicle, route) -> list[str]:
        """The roads of a route already driven, through the one the shuttle is on; empty when it is off the route."""
        # The public position is authoritative, including a hold rolled back to
        # the previous presentation boundary. Find its occupied road geometrically.
        nearest = (math.inf, 0)
        for index, path in enumerate(route.edge_paths_m):
            for a, b in zip(path, path[1:]):
                dx, dy = b.x_m-a.x_m, b.y_m-a.y_m
                length_sq = dx*dx + dy*dy
                if not length_sq:
                    continue
                t = max(0, min(1, ((vehicle.position.x_m-a.x_m)*dx +
                                  (vehicle.position.y_m-a.y_m)*dy) / length_sq))
                distance = math.hypot(vehicle.position.x_m-a.x_m-t*dx, vehicle.position.y_m-a.y_m-t*dy)
                if distance < nearest[0]:
                    nearest = (distance, index)
        return route.directed_edge_ids[:nearest[1]+1] if nearest[0] <= 0.01 else []

    def continuation_preview(self, snapshot: FullSnapshot, vehicle_id: str,
                             draft: RouteEditRequest | None = None) -> RouteEditPreview:
        """Validate a detour that still contains the shuttle's current road and driven prefix."""
        vehicle = next(v for v in snapshot.vehicles if v.vehicle_id == vehicle_id)
        route = next(r for r in snapshot.routes if r.route_id == vehicle.route_id)
        request = dict(route_id=route.route_id, expected_route_revision=draft.expected_route_revision if draft else route.revision,
                       draft_revision=draft.draft_revision if draft else 1)
        if self._network is None:
            return self.preview(snapshot, RouteEditRequest(**request), retain=False)
        prefix = self._driven_prefix(vehicle, route)
        visited = self._stop_visits(prefix, self._walk(prefix, self._edges()))
        remaining_stops = [self._fixture["stop_approach_edge_ids"][stop]
                           for stop in route.required_stop_ids if not visited.get(stop)]
        ids, unreached = self._network.continuation(prefix, self._fixture["destination_node_id"], remaining_stops,
                                                    self._closed(snapshot), self._fixture,
                                                    draft.waypoint_edge_ids if draft else [])
        result = self.preview(snapshot, RouteEditRequest(**request,
                              directed_edge_ids=ids or route.directed_edge_ids), retain=False)
        if not ids:
            message = (f"The shuttle can't reach {self._network.road_name(unreached)} from where it is held and still complete its loop."
                       if unreached else "No detour connects to the shuttle's current road")
            result.checks.append(RouteCheck(check="GEOMETRY", status="FAIL", message=message, feature_id=unreached,
                                            edge_ids=[unreached] if unreached else []))
            result.validation, result.approvable = "FAIL", False
        return result

    def chosen_continuation(self, snapshot: FullSnapshot, vehicle_id: str, ids: list[str]) -> RouteEditPreview:
        """Validate the operator's own detour: exactly these roads, from where the shuttle stands."""
        vehicle = next(v for v in snapshot.vehicles if v.vehicle_id == vehicle_id)
        route = next(r for r in snapshot.routes if r.route_id == vehicle.route_id)
        result = self.preview(snapshot, RouteEditRequest(route_id=route.route_id, expected_route_revision=route.revision,
                                                         draft_revision=1, directed_edge_ids=ids), retain=False)
        prefix = self._driven_prefix(vehicle, route)
        if not prefix or ids[:len(prefix)] != prefix:
            result.checks.append(RouteCheck(check="GEOMETRY", status="FAIL",
                                            message="This detour does not continue from the shuttle's current road"))
            result.validation, result.approvable = "FAIL", False
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
        self._check_waypoints(request)
        edges = self._edges()
        closed = self._closed(snapshot)
        unreached = None
        with self._lock:
            self._unreached = None
            ids = self._candidate(request, edges, closed)
            unreached = self._unreached
        nodes = self._walk(ids, edges)
        config = self._fixture
        checks: list[RouteCheck] = []
        network = self._network
        def road(identifier: str | None) -> str:
            return network.road_name(identifier) if network and identifier else "this road"
        def check(name: str, status: str, message: str, feature: str | None = None, edges_at_fault: list[str] | None = None) -> None:
            checks.append(RouteCheck(check=name, status=status, message=message, feature_id=feature,
                                     edge_ids=edges_at_fault if edges_at_fault is not None else [feature] if feature in edges else []))

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
        elif unreached:
            check("GEOMETRY", "FAIL", f"The shuttle can't reach {road(unreached)} and still complete its loop: it would have to U-turn or drive against a one-way road.", unreached)
        elif not ids:
            check("GEOMETRY", "FAIL", "No route connects the requested nodes within the curated network")
        elif not nodes or nodes[0] != config["start_node_id"] or nodes[-1] != config["destination_node_id"]:
            check("GEOMETRY", "FAIL", "Segments are discontinuous or do not reach the route endpoints")
        else:
            check("GEOMETRY", "PASS", "Directed segments form a continuous endpoint-to-endpoint path")
        check("DIRECTION", "FAIL" if wrong_way else "PASS" if nodes else "UNKNOWN",
              direction_message if wrong_way or nodes else "Direction cannot be checked without a path", missing)
        closed_hits = list(dict.fromkeys(identifier for identifier in ids if identifier in closed))
        closed_hit = closed_hits[0] if closed_hits else None
        check("CLOSURE", "FAIL" if closed_hit else "PASS",
              (f"{capitalised(road(closed_hit))} is closed: the route can't use it." if network else "Route uses an active closure") if closed_hit else "Active closure avoided",
              closed_hit, closed_hits)
        visits = self._stop_visits(ids, nodes)
        coverage = {stop: bool(visits.get(stop)) for stop in route.required_stop_ids}
        missed = next((stop for stop, served in coverage.items() if not served), None)
        stop_positions = []
        for stop in route.required_stop_ids if not missed else []:
            later = [index for index in visits[stop] if not stop_positions or index > stop_positions[-1]]
            stop_positions.append(later[0] if later else -1)
        out_of_order = not missed and -1 in stop_positions
        check("STOPS", "FAIL" if missed or out_of_order else "PASS",
              (f"The route misses {stop_label(missed)}." if network else "Required stop is missing") if missed else "Required stops are out of order" if out_of_order else "Required stops are served in order",
              missed or (route.required_stop_ids[0] if out_of_order else None))
        outside = next((str(node) for node in nodes if node not in config["operating_node_ids"]), None)
        check("OPERATING_AREA", "FAIL" if outside else "PASS", "Route leaves the configured operating area" if outside else "Route stays inside the configured operating area", outside)
        if network:
            self._network_checks(ids, check)
        else:
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
                                   original=original, edited=edited,
                                   edge_paths=self._edge_paths(ids) if nodes else [],
                                   service_impact=self._service_impact(snapshot, route.route_id, original, edited))
        if retain:
            with self._lock:
                self._previews[preview.preview_id] = (request.model_copy(deep=True), preview.model_copy(deep=True))
                while len(self._previews) > 256:
                    self._previews.popitem(last=False)
        return preview

    def _check_waypoints(self, request: RouteEditRequest) -> None:
        if not request.waypoint_edge_ids:
            return
        if self._network is None:
            raise TrainingStoreError("UNKNOWN_CONSTRAINT", "This route has no operating road network to pick from")
        if any(key not in self._network.edges for key in request.waypoint_edge_ids):
            raise TrainingStoreError("INVALID_REFERENCE", "Waypoint is not a road of the operating network")

    def _network_checks(self, ids: list[str], check) -> None:
        """Every road of the path against its own metadata, and every turn between two of them."""
        network, config = self._network, self._fixture
        known = [identifier for identifier in dict.fromkeys(ids) if identifier in network.edges]
        results = {identifier: network.fit(identifier, config) for identifier in known}
        failed = [identifier for identifier in known if results[identifier][0] == "FAIL"]
        unknown = [identifier for identifier in known if results[identifier][0] == "UNKNOWN"]
        at_fault = failed or unknown
        if at_fault:
            check("VEHICLE_FIT", "FAIL" if failed else "UNKNOWN", results[at_fault[0]][1], at_fault[0], at_fault)
        else:
            check("VEHICLE_FIT", "PASS", "The shuttle fits on every road of this route.")
        # The shuttle goes round: its last road leads onto its first
        turns = list(zip(ids, ids[1:]))
        if ids and self._fixture["start_node_id"] == self._fixture["destination_node_id"]:
            turns.append((ids[-1], ids[0]))
        bad = next(((a, b) for a, b in turns if a in network.edges and b in network.edges and not network.turn_allowed(a, b)), None)
        if bad:
            check("TURNS", "FAIL", network.turn_description(*bad), f"{bad[0]}>{bad[1]}", list(bad))
        else:
            check("TURNS", "PASS", "Every turn on this route is one a shuttle can make.")

    def _edge_paths(self, ids: list[str]) -> list[list[dict]]:
        """Per-edge drawn geometry, where the road's shape is known."""
        if not self._network or any(identifier not in self._network.edges for identifier in ids):
            return []
        return [[dict(x=x, y=y, z=0.0) for x, y in self._network.edges[identifier]["path"]] for identifier in ids]

    def _service_impact(self, snapshot: FullSnapshot, route_id: str, original: RouteMetrics | None,
                        edited: RouteMetrics | None) -> list[RouteServiceImpact]:
        """How many more loops each assigned shuttle's charge buys, on either route, above its reserve."""
        def loops(vehicle, metrics: RouteMetrics | None) -> int | None:
            if metrics is None or metrics.energy_kwh <= 0:
                return None
            spare = vehicle.battery_kwh - reserve_kwh(ENERGY_R1, vehicle.battery_capacity_kwh)
            return max(0, int(spare // metrics.energy_kwh))
        return [RouteServiceImpact(vehicle_id=v.vehicle_id, battery_kwh=v.battery_kwh, loops_on_original=loops(v, original),
                                   loops_on_edited=loops(v, edited))
                for v in snapshot.vehicles if v.route_id == route_id]

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
            refreshed = self.for_route(snapshot, draft.route_id).preview(snapshot, draft, retain=False)
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
                            "energy_kwh": refreshed.edited.energy_kwh,
                            "original": refreshed.original.model_dump(mode="json") if refreshed.original else None,
                            "edited": refreshed.edited.model_dump(mode="json")})
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
        refreshed = self.for_route(snapshot, draft.route_id).preview(snapshot, draft, retain=False)
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
