"""Nearest-node route planning over the cached committed-map road graph."""

from __future__ import annotations

import math
from collections import OrderedDict
from functools import lru_cache
from threading import RLock
from uuid import uuid4

import networkx as nx

from src.routing.road_graph import RoadGraphRepository, get_road_graph_repository
from src.schemas.route import (
    CreateRouteRequest,
    RoutePoint,
    RouteResponse,
    SnappedRoutePoint,
)
from src.schemas.scene import SceneBounds, ScenePoint
from src.services.scene_repository import SceneRepository, get_scene_repository


SNAP_WARNING_DISTANCE_METRES = 25.0
_DURATION_FALLBACK_SPEED_KPH = 30.0


class RoutePlanningError(RuntimeError):
    """Base error for a syntactically valid route request that cannot be planned."""


class SceneRevisionMismatchError(RoutePlanningError):
    pass


class RouteNotFoundError(RoutePlanningError):
    pass


class RoutePlanNotFoundError(RoutePlanningError):
    pass


class RoutePlanStore:
    """Bounded in-memory ownership for previews accepted by a simulation."""

    def __init__(self, maximum_routes: int = 256) -> None:
        if maximum_routes <= 0:
            raise ValueError("maximum_routes must be greater than zero")
        self._maximum_routes = maximum_routes
        self._routes: OrderedDict[str, RouteResponse] = OrderedDict()
        self._lock = RLock()

    def put(self, route: RouteResponse) -> None:
        with self._lock:
            self._routes[route.route_id] = route.model_copy(deep=True)
            self._routes.move_to_end(route.route_id)
            while len(self._routes) > self._maximum_routes:
                self._routes.popitem(last=False)

    def get(self, route_id: str) -> RouteResponse:
        with self._lock:
            route = self._routes.get(route_id)
            if route is None:
                raise RoutePlanNotFoundError(
                    f"Planned route '{route_id}' was not found or has expired."
                )
            self._routes.move_to_end(route_id)
            return route.model_copy(deep=True)

    def clear(self) -> None:
        with self._lock:
            self._routes.clear()


class RoutePlanner:
    def __init__(
        self,
        scene_repository: SceneRepository,
        graph_repository: RoadGraphRepository,
        route_store: RoutePlanStore | None = None,
    ) -> None:
        self._scene_repository = scene_repository
        self._graph_repository = graph_repository
        self._route_store = route_store or get_route_plan_store()

    def plan(self, request: CreateRouteRequest) -> RouteResponse:
        scene = self._scene_repository.metadata(request.scene_id)
        if request.scene_revision != scene.scene_revision:
            raise SceneRevisionMismatchError(
                "The requested scene revision is stale; reload the scene before planning. "
                f"Expected '{scene.scene_revision}', got '{request.scene_revision}'."
            )

        snapshot = self._graph_repository.get()
        graph = snapshot.graph
        start_node, start_distance = _nearest_node(graph, request.start)
        destination_node, destination_distance = _nearest_node(
            graph, request.destination
        )
        try:
            node_path = nx.shortest_path(
                graph,
                source=start_node,
                target=destination_node,
                weight="length_m",
                method="dijkstra",
            )
        except (nx.NetworkXNoPath, nx.NodeNotFound) as error:
            raise RouteNotFoundError(
                "No drivable route connects the snapped start and destination."
            ) from error

        polyline = [
            ScenePoint(
                x=float(graph.nodes[node_id]["x"]),
                y=float(graph.nodes[node_id]["y"]),
                z=float(graph.nodes[node_id].get("z", 0.0)),
            )
            for node_id in node_path
        ]
        distance, estimated_duration, used_speed_fallback = _path_metrics(
            graph, node_path
        )
        warnings = list(snapshot.warnings)
        if start_distance > SNAP_WARNING_DISTANCE_METRES:
            warnings.append(
                f"Start snapped {start_distance:.1f} m to the nearest routable node."
            )
        if destination_distance > SNAP_WARNING_DISTANCE_METRES:
            warnings.append(
                "Destination snapped "
                f"{destination_distance:.1f} m to the nearest routable node."
            )
        if used_speed_fallback:
            warnings.append(
                "Estimated duration uses a 30 km/h fallback for segments without a speed estimate."
            )

        response = RouteResponse(
            route_id=str(uuid4()),
            scene_id=request.scene_id,
            scene_revision=scene.scene_revision,
            transform_revision=scene.transform_revision,
            cost=request.cost,
            routing_mode=snapshot.routing_mode,
            snapped_start=_snapped_point(graph, start_node, start_distance),
            snapped_destination=_snapped_point(
                graph, destination_node, destination_distance
            ),
            polyline=polyline,
            distance=round(distance, 3),
            estimated_duration=round(estimated_duration, 3),
            bounds=SceneBounds(
                min_x=min(point.x for point in polyline),
                min_y=min(point.y for point in polyline),
                max_x=max(point.x for point in polyline),
                max_y=max(point.y for point in polyline),
            ),
            warnings=warnings,
        )
        self._route_store.put(response)
        return response


def _nearest_node(graph: nx.MultiDiGraph, point: RoutePoint) -> tuple[int, float]:
    if graph.number_of_nodes() == 0:
        raise RoutePlanningError("The road graph contains no routable nodes")
    node_id, squared_distance = min(
        (
            (
                int(node_id),
                (float(data["x"]) - point.x) ** 2
                + (float(data["y"]) - point.y) ** 2,
            )
            for node_id, data in graph.nodes(data=True)
        ),
        key=lambda candidate: (candidate[1], candidate[0]),
    )
    return node_id, math.sqrt(squared_distance)


def _snapped_point(
    graph: nx.MultiDiGraph, node_id: int, snapping_distance: float
) -> SnappedRoutePoint:
    node = graph.nodes[node_id]
    return SnappedRoutePoint(
        node_id=node_id,
        x=float(node["x"]),
        y=float(node["y"]),
        z=float(node.get("z", 0.0)),
        snapping_distance=round(snapping_distance, 3),
    )


def _path_metrics(
    graph: nx.MultiDiGraph, node_path: list[int]
) -> tuple[float, float, bool]:
    distance = 0.0
    duration = 0.0
    used_speed_fallback = False
    for source, target in zip(node_path, node_path[1:]):
        parallel_edges = graph.get_edge_data(source, target)
        if not parallel_edges:
            raise RoutePlanningError(
                f"Road graph path is missing segment {source}->{target}"
            )
        edge = min(
            parallel_edges.values(), key=lambda candidate: float(candidate["length_m"])
        )
        length = float(edge["length_m"])
        raw_speed = edge.get("speed_estimate_kph")
        speed = float(raw_speed) if raw_speed is not None else _DURATION_FALLBACK_SPEED_KPH
        if raw_speed is None:
            used_speed_fallback = True
        distance += length
        duration += length / (speed / 3.6)
    return distance, duration, used_speed_fallback


@lru_cache
def get_route_plan_store() -> RoutePlanStore:
    return RoutePlanStore()


def get_planned_route(route_id: str) -> RouteResponse:
    return get_route_plan_store().get(route_id)


def get_route_planner() -> RoutePlanner:
    return RoutePlanner(
        get_scene_repository(),
        get_road_graph_repository(),
        get_route_plan_store(),
    )


__all__ = [
    "RouteNotFoundError",
    "RoutePlanNotFoundError",
    "RoutePlanStore",
    "RoutePlanner",
    "RoutePlanningError",
    "SceneRevisionMismatchError",
    "get_planned_route",
    "get_route_plan_store",
    "get_route_planner",
]
