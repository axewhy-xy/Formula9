"""Route geometry in snapshots: one drawable path per directed edge, joined end to end."""

import pytest
from pydantic import ValidationError

from contracts.v2.models import RouteRevision
from src.services.asset_loader import load_routes_from_json
from src.services.integrated_actor import IntegratedSessionActor
from src.services.session_actor import SessionActor
from src.services.spatial_session_actor import SpatialSessionActor


def assert_joined(route):
    assert len(route.edge_paths_m) == len(route.directed_edge_ids)
    for before, after in zip(route.edge_paths_m, route.edge_paths_m[1:]):
        assert before[-1] == after[0], "consecutive edges must meet"


def test_fleet_loop_routes_carry_their_waypoints():
    routes = load_routes_from_json()
    snapshot = SessionActor(seed=1000, route=routes).generate_snapshot()
    assert len(snapshot.routes) == len(routes)
    for route, source in zip(snapshot.routes, routes):
        assert_joined(route)
        drawn = [(p.x_m, p.y_m) for p in [path[0] for path in route.edge_paths_m] + [route.edge_paths_m[-1][-1]]]
        assert drawn == [(w.x, w.y) for w in source["waypoints"]]


def test_spatial_routes_follow_the_scene_road_surfaces():
    actor = SpatialSessionActor()
    route = actor.generate_snapshot().routes[0]
    assert_joined(route)
    surfaces = {s.directed_edge_id: s for s in actor.package.manifest.road_surfaces}
    for edge_id, path in zip(route.directed_edge_ids, route.edge_paths_m):
        assert [[p.x_m, p.y_m, p.z_m] for p in path] == [list(sample) for sample in surfaces[edge_id].path_samples_m]


def test_geometry_must_match_the_edges():
    base = {"route_id": "route-a", "revision": 1, "scene_revision": "scene-r1", "directed_edge_ids": ["edge-1", "edge-2"],
            "required_stop_ids": [], "distance_m": 10.0, "estimated_duration_s": 2.0}
    point = {"x_m": 0.0, "y_m": 0.0, "z_m": 0.0}
    assert RouteRevision.model_validate(base).edge_paths_m == []
    with pytest.raises(ValidationError):
        RouteRevision.model_validate({**base, "edge_paths_m": [[point, point]]})


def test_integrated_routes_are_drawable_and_closure_is_on_the_primary_route():
    snapshot = IntegratedSessionActor(seed=1000, session_id="geometry-check").generate_snapshot()
    for route in snapshot.routes:
        assert_joined(route)
    primary = snapshot.routes[0]
    closed = set(snapshot.disruptions[0].affected_edge_ids)
    assert closed and closed <= set(primary.directed_edge_ids), "the closure must be drawable on a sent route"


def test_integrated_primary_route_places_its_stop_on_the_route():
    snapshot = IntegratedSessionActor(seed=1000, session_id="stop-check").generate_snapshot()
    primary = snapshot.routes[0]
    assert [stop.stop_id for stop in primary.stops_m] == primary.required_stop_ids == ["stop-terminal"]
    corners = {(p.x_m, p.y_m) for path in primary.edge_paths_m for p in path}
    assert (primary.stops_m[0].position.x_m, primary.stops_m[0].position.y_m) in corners


def test_stops_m_may_only_place_required_stops():
    route = IntegratedSessionActor(seed=1000, session_id="stop-check").generate_snapshot().routes[0]
    data = route.model_dump(mode="json")
    data["stops_m"][0]["stop_id"] = "stop-elsewhere"
    with pytest.raises(ValueError):
        RouteRevision.model_validate(data)
