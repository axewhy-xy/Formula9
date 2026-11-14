"""Route geometry in snapshots: one drawable path per directed edge, joined end to end."""

import pytest
from pydantic import ValidationError

from contracts.v2.models import RouteRevision
from src.services.asset_loader import load_routes_from_json
from src.services.integrated_actor import IntegratedSessionActor, get_workflow_definition
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


def test_integrated_routes_are_drawable_and_no_road_is_closed_at_the_start():
    snapshot = IntegratedSessionActor(seed=1000, session_id="geometry-check").generate_snapshot()
    for route in snapshot.routes:
        assert_joined(route)
    assert snapshot.routes[0].route_id == "route-peer-a"
    assert snapshot.disruptions == [], "no road block is scripted on shuttle-01's route"


def test_integrated_routes_place_their_stops_on_the_route():
    snapshot = IntegratedSessionActor(seed=1000, session_id="stop-check").generate_snapshot()
    definition = get_workflow_definition()
    assert [route.route_id for route in snapshot.routes] == definition.route_ids
    for route in snapshot.routes:
        # Real LTA bus stops on the loop, named by stop code, in the order the loop serves them
        required = list(definition.routes.route_config(route.route_id)["required_stops"])
        assert [stop.stop_id for stop in route.stops_m] == route.required_stop_ids == required
        assert required and all(stop.split("-")[0].isdigit() and len(stop.split("-")[0]) == 5 for stop in required)
        corners = {(p.x_m, p.y_m) for path in route.edge_paths_m for p in path}
        assert all((stop.position.x_m, stop.position.y_m) in corners for stop in route.stops_m), route.route_id


def test_stops_m_may_only_place_required_stops():
    route = IntegratedSessionActor(seed=1000, session_id="stop-check").generate_snapshot().routes[0]
    data = route.model_dump(mode="json")
    data["stops_m"][0]["stop_id"] = "stop-elsewhere"
    with pytest.raises(ValueError):
        RouteRevision.model_validate(data)


def test_every_shuttle_pulls_in_at_every_stop_on_its_route():
    actor = IntegratedSessionActor(seed=1000, session_id="stops")
    routes = {route.route_id: route for route in actor.domain.routes}
    assert len(routes["route-peer-a"].stops_m) == 3
    assert len({shuttle.route_ID for shuttle in actor.shuttles}) == 5, "every shuttle has its own route"
    pairs = (("route-peer-b", "route-peer-d"), ("route-peer-c", "route-peer-e"))
    for first, second in pairs:
        # In the order the loop serves them: the loop's own stop, where it closes, last
        own = [{stop.stop_id for stop in routes[route].stops_m[:-1]} for route in (first, second)]
        assert all(own) and not own[0] & own[1], "shuttles sharing a loop have different stops"
        assert routes[first].stops_m[-1].stop_id == routes[second].stops_m[-1].stop_id, "they share where the loop closes"
    actor.is_paused = False
    actor.modes = {index: "IN_SERVICE" for index in actor.modes}
    # Shuttle-05 starts low and would leave for the depot on its own after a loop
    for shuttle in actor.shuttles:
        shuttle.battery = 100.0
    visited = {shuttle.ID: set() for shuttle in actor.shuttles}
    for _ in range(9000):
        actor.tick()
        for shuttle in actor.shuttles:
            if actor.modes[shuttle.ID] != "DWELLING":
                continue
            for stop in routes[shuttle.route_ID].stops_m:
                if abs(shuttle.position.x - stop.position.x_m) < 0.5 and abs(shuttle.position.y - stop.position.y_m) < 0.5:
                    visited[shuttle.ID].add(stop.stop_id)
    for shuttle in actor.shuttles:
        assert visited[shuttle.ID] == {stop.stop_id for stop in routes[shuttle.route_ID].stops_m}, shuttle.ID
