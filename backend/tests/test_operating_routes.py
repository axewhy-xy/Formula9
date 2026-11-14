"""The routing exercise end to end: pick roads, read the verdict, approve, drive it, read the outcome."""

import asyncio
import json
import math
from uuid import uuid4

from fastapi.testclient import TestClient
import pytest

from src.api.routes.training_v2 import get_actor_id, get_training_store
from src.app import app
from src.persistence.training_store import TrainingStore
from src.routing.build_operating_network import road_metadata
from src.routing.operating_network import OperatingNetwork, twin_id
from src.services.integrated_actor import SCENARIO_SELECTOR, get_workflow_definition
from src.services.training_sessions import TrainingSessionService


ROUTE = "route-peer-a"
SPEED_UP = 8


async def manual_clock(self, session):
    await asyncio.Event().wait()


@pytest.fixture
def runtime(tmp_path, monkeypatch):
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)
    previous = dict(app.dependency_overrides)
    try:
        with TrainingStore(tmp_path/"routes.sqlite") as store:
            app.dependency_overrides[get_training_store] = lambda: store
            app.dependency_overrides[get_actor_id] = lambda: "route-operator"
            with TestClient(app) as client:
                created = client.post("/api/v2/sessions", headers={"Idempotency-Key": "routes"},
                                      json={"scenario_revision_id": SCENARIO_SELECTOR, "seed": 1000})
                assert created.status_code == 201
                yield client, store, app.state.training_services[store], created.json()["session_id"]
    finally:
        app.dependency_overrides.clear()
        app.dependency_overrides.update(previous)


def network() -> OperatingNetwork:
    return get_workflow_definition().routes._network


def preview(runtime, **changes) -> dict:
    client, _, _, session_id = runtime
    response = client.post(f"/api/v2/sessions/{session_id}/route-previews",
                           json={"route_id": ROUTE, "expected_route_revision": 1, "draft_revision": 1, **changes})
    assert response.status_code == 200, response.text
    return response.json()


def approve(runtime, draft: dict, vehicle_ids: tuple[str, ...] = ("shuttle-01",)) -> dict:
    client, _, _, session_id = runtime
    response = client.post(f"/api/v2/sessions/{session_id}/route-approvals", json={
        "preview_id": draft["preview_id"], "preview_revision": draft["preview_revision"],
        "command_id": uuid4().hex, "idempotency_key": uuid4().hex,
        "target_vehicle_ids": list(vehicle_ids), "reason": "Round the closure by the roads I chose"})
    assert response.status_code == 200, response.text
    return response.json()


def control(runtime, kind: str) -> dict:
    client, _, _, session_id = runtime
    snapshot = client.get(f"/api/v2/sessions/{session_id}/snapshot").json()
    response = client.post(f"/api/v2/sessions/{session_id}/commands", json={
        "contract_version": "2.0", "command_id": uuid4().hex, "idempotency_key": uuid4().hex,
        "session_id": session_id, "command_type": kind, "expected_epoch": snapshot["session_epoch"],
        "expected_revision": snapshot["session"]["aggregate_revision"], "target_type": "SESSION",
        "target_id": session_id, "payload": {}, "reason": "Exercise control"})
    assert response.status_code == 202, response.text
    return response.json()


def uncurated_road(edges: dict, config: dict) -> str:
    """A road off every scripted chain that the shuttle may use, near enough to detour by."""
    chains = get_workflow_definition().network["chains"]
    curated = {edge for ids in chains.values() for edge in ids}
    start = get_workflow_definition().network["nodes"][str(config["start_node_id"])]
    usable = [key for key in edges if key not in curated and network().fit_status(key, config) == "PASS"
              and edges[key]["road"] and twin_id(key) in edges]
    return min(usable, key=lambda key: math.dist(edges[key]["path"][0], start))


def test_the_editor_offers_every_road_with_names_and_no_more_than_it_knows(runtime):
    client, _, _, session_id = runtime
    context = client.get(f"/api/v2/sessions/{session_id}/routes/{ROUTE}/editor").json()
    assert len(context["edges"]) > 1000, "well beyond the scripted detours"
    assert not any(edge["closed"] for edge in context["edges"]), "no road is closed before the start"
    assert any(edge["road_name"] for edge in context["edges"])
    assert {edge["fit"] for edge in context["edges"]} >= {"PASS", "FAIL", "UNKNOWN"}
    assert all(len(edge["path"]) >= 2 for edge in context["edges"])
    assert [stop["name"] for stop in context["stops"]] == ["Toa Payoh Police Centre", "Braddell Station Blk 106", "Blk 101c Carpark"]


def test_a_road_off_the_scripted_detours_can_be_previewed_approved_and_driven(runtime):
    client, store, service, session_id = runtime
    config = get_workflow_definition().routes._fixture
    edges = network().edges
    road = uncurated_road(edges, config)
    draft = preview(runtime, waypoint_edge_ids=[road])
    assert draft["validation"] == "PASS", draft["checks"]
    assert draft["approvable"]
    assert road in draft["directed_edge_ids"] or twin_id(road) in draft["directed_edge_ids"]
    assert all(draft["edited"]["stop_coverage"].values())
    assert len(draft["edge_paths"]) == len(draft["directed_edge_ids"])
    assert draft["edited"]["duration_s"] > draft["edited"]["distance_m"] / 10
    assert draft["service_impact"][0]["vehicle_id"] == "shuttle-01"
    assert approve(runtime, draft)["status"] == "APPLIED"
    snapshot = client.get(f"/api/v2/sessions/{session_id}/snapshot").json()
    route = snapshot["routes"][0]
    assert route["directed_edge_ids"] == draft["directed_edge_ids"]
    # The shape drawn is the shape of each road, not a straight line between junctions
    assert route["edge_paths_m"][0][0]["x_m"] == edges[draft["directed_edge_ids"][0]]["path"][0][0]
    actor = service.sessions[session_id].actor
    assert actor._routes[ROUTE]["total_length"] == pytest.approx(draft["edited"]["distance_m"], abs=0.01)
    assert control(runtime, "START")["status"] == "APPLIED"


@pytest.mark.parametrize("road_filter,failed_check", [
    (lambda edge: edge["access"] == "closed_to_buses", "VEHICLE_FIT"),
    (lambda edge: edge["height_m"] is not None and edge["height_m"] < 2.8, "VEHICLE_FIT"),
])
def test_a_road_the_shuttle_cannot_use_is_named_and_highlighted(runtime, road_filter, failed_check):
    edges = network().edges
    road = next(key for key, edge in edges.items() if road_filter(edge))
    draft = preview(runtime, waypoint_edge_ids=[road])
    assert not draft["approvable"]
    check = next(c for c in draft["checks"] if c["check"] == failed_check and c["status"] == "FAIL")
    assert road in check["edge_ids"] or twin_id(road) in check["edge_ids"]
    assert "the shuttle" in check["message"].lower() or "closed to buses" in check["message"]
    assert "road-" not in check["message"] and ":forward" not in check["message"]


def test_unknown_metadata_blocks_approval_and_says_what_is_missing(runtime):
    edges = network().edges
    road = next(key for key, edge in edges.items() if edge["width_m"] is None)
    draft = preview(runtime, waypoint_edge_ids=[road])
    assert not draft["approvable"]
    check = next(c for c in draft["checks"] if c["check"] == "VEHICLE_FIT")
    assert check["status"] in {"UNKNOWN", "FAIL"} and check["edge_ids"]
    assert "No width is recorded" in check["message"] or check["status"] == "FAIL"


def test_shuttle_01s_main_road_is_open_at_the_start(runtime):
    # No road block is scripted on shuttle-01's route: its main road can be chosen and the session started
    # without a pre-trip approval
    config = get_workflow_definition().routes._fixture
    main = config["closed_directed_edge_ids"][0]
    draft = preview(runtime, waypoint_edge_ids=[main])
    check = next(c for c in draft["checks"] if c["check"] == "CLOSURE")
    assert check["status"] == "PASS" and draft["approvable"]
    assert control(runtime, "START")["status"] == "APPLIED"


def test_the_planner_never_u_turns_and_serves_stops_from_their_kerb():
    definition = get_workflow_definition()
    config = definition.routes._fixture
    net = network()
    stops = [config["stop_approach_edge_ids"][stop] for stop in config["required_stops"]]
    road = uncurated_road(net.edges, config)
    planned, unreached = net.plan(config["start_node_id"], config["destination_node_id"], stops, [road],
                                  set(config["closed_directed_edge_ids"]), config)
    assert unreached is None and planned
    for first, second in zip(planned, planned[1:]):
        assert net.edges[first]["target"] == net.edges[second]["source"]
        assert net.turn_allowed(first, second), (first, second)
    for options in stops:
        assert set(options) & set(planned)
    # The same plan twice, for a preview and its later check at approval
    assert net.plan(config["start_node_id"], config["destination_node_id"], stops, [road],
                    set(config["closed_directed_edge_ids"]), config)[0] == planned


def test_a_stop_passed_on_the_wrong_side_of_the_road_is_not_served(runtime):
    definition = get_workflow_definition()
    service = definition.routes
    config = service._fixture
    stop, node = next(iter(config["required_stops"].items()))
    ids = [edge for edge in network().edges if network().edges[edge]["target"] == node]
    against_kerb = [edge for edge in ids if edge not in config["stop_approach_edge_ids"][stop]]
    for edge in against_kerb:
        assert not service._stop_visits([edge], [network().edges[edge]["source"], node])[stop]


def test_operating_metadata_prefers_what_is_recorded_and_admits_what_is_not():
    tagged = road_metadata({"highway": "service", "width": "2.6", "maxheight": "2.2"}, True, True, curated=False, curated_width_m=3.5)
    assert (tagged["width_m"], tagged["width_source"], tagged["height_m"], tagged["height_source"]) == (2.6, "osm:width", 2.2, "osm:maxheight")
    lanes = road_metadata({"highway": "residential", "lanes": "2", "oneway": "yes"}, True, True, curated=False, curated_width_m=3.5)
    assert (lanes["width_m"], lanes["width_source"]) == (7.0, "osm:lanes")
    bare = road_metadata({"highway": "service"}, True, True, curated=False, curated_width_m=3.5)
    assert bare["width_m"] is None and bare["width_source"] is None
    enclosed = road_metadata({"highway": "residential", "covered": "yes"}, False, True, curated=False, curated_width_m=3.5)
    assert enclosed["height_m"] is None
    assert road_metadata({"highway": "secondary", "bus": "no"}, True, True, curated=False, curated_width_m=3.5)["access"] == "closed_to_buses"


# Each peer route and the shuttle assigned to it
PEERS = {"route-peer-b": "shuttle-02", "route-peer-c": "shuttle-03", "route-peer-d": "shuttle-04", "route-peer-e": "shuttle-05"}


def test_every_peer_route_has_its_own_editor_data_and_its_roads_pass(runtime):
    client, _, _, session_id = runtime
    definition = get_workflow_definition()
    assert definition.data["vehicle_assignments"][1:] == list(PEERS)
    for route_id in PEERS:
        config = definition.routes.route_config(route_id)
        context = client.get(f"/api/v2/sessions/{session_id}/routes/{route_id}/editor").json()
        assert context["route_id"] == route_id
        # A peer's loop starts and ends at its own stop, which it serves last
        assert context["start_node_id"] == context["destination_node_id"] == config["start_node_id"]
        assert [stop["stop_id"] for stop in context["stops"]] == list(config["required_stops"])
        assert config["required_stops"][context["stops"][-1]["stop_id"]] == config["start_node_id"]
        assert context["original_directed_edge_ids"] == config["original_directed_edge_ids"]
        draft = preview(runtime, route_id=route_id, directed_edge_ids=config["original_directed_edge_ids"])
        assert draft["validation"] == "PASS", (route_id, [c for c in draft["checks"] if c["status"] != "PASS"])
        assert all(draft["edited"]["stop_coverage"].values())


def test_a_peer_route_is_edited_approved_and_driven_as_shuttle_01s_is(runtime):
    client, store, service, session_id = runtime
    route_id, vehicle_id = "route-peer-b", PEERS["route-peer-b"]
    definition = get_workflow_definition()
    config = definition.routes.route_config(route_id)
    edges = network().edges
    # The nearest road off its loop, near where the loop starts, that the shuttle may use and can drive
    # round its loop by
    start = definition.network["nodes"][str(config["start_node_id"])]
    loop = set(config["original_directed_edge_ids"])
    roads = sorted((key for key in edges if key not in loop and twin_id(key) not in loop and edges[key]["road"]
                    and twin_id(key) in edges and network().fit_status(key, config) == "PASS"),
                   key=lambda key: math.dist(edges[key]["path"][0], start))
    drafts = (preview(runtime, route_id=route_id, waypoint_edge_ids=[road]) for road in roads[:40])
    road, draft = next((road, draft) for road, draft in zip(roads, drafts) if draft["approvable"])
    assert road in draft["directed_edge_ids"] or twin_id(road) in draft["directed_edge_ids"]
    assert all(draft["edited"]["stop_coverage"].values())
    assert [impact["vehicle_id"] for impact in draft["service_impact"]] == [vehicle_id]
    assert approve(runtime, draft, (vehicle_id,))["status"] == "APPLIED"
    snapshot = client.get(f"/api/v2/sessions/{session_id}/snapshot").json()
    route = next(r for r in snapshot["routes"] if r["route_id"] == route_id)
    assert route["revision"] == 2 and route["directed_edge_ids"] == draft["directed_edge_ids"]
    # The actor drives it, and pulls in at the peer's own stops: its loop's stop at the loop's end
    actor = service.sessions[session_id].actor
    assert actor._routes[route_id]["total_length"] == pytest.approx(draft["edited"]["distance_m"], abs=0.01)
    stops = list(config["required_stops"])
    assert actor.stop_names[route_id].pop("end") == stops[-1]
    assert sorted(actor.stop_names[route_id].values()) == sorted(stops[:-1])


def test_every_scripted_route_stays_valid_on_the_network(runtime):
    definition = get_workflow_definition()
    chains = definition.network["chains"]
    for name in ("bypass-b", "bypass-c"):
        draft = preview(runtime, directed_edge_ids=chains[name] + chains["return"])
        assert draft["validation"] == "PASS", (name, [c for c in draft["checks"] if c["status"] != "PASS"])


def test_the_outcome_reports_what_the_shuttle_did_against_what_was_approved(runtime):
    client, store, service, session_id = runtime
    url = f"/api/v2/sessions/{session_id}/routes/{ROUTE}/outcome"
    assert client.get(url).json()["state"] == "NOT_APPROVED"
    definition = get_workflow_definition()
    chains = definition.network["chains"]
    draft = preview(runtime, directed_edge_ids=chains["bypass-b"] + chains["return"])
    assert approve(runtime, draft)["status"] == "APPLIED"
    approved = client.get(url).json()
    assert approved["state"] == "APPROVED" and approved["vehicle_id"] == "shuttle-01"
    assert approved["planned"]["duration_s"] == draft["edited"]["duration_s"]
    assert approved["original"]["duration_s"] == draft["original"]["duration_s"]
    assert approved["reason"]
    assert control(runtime, "START")["status"] == "APPLIED"
    assert client.get(url).json()["state"] == "RUNNING"

    def loop_done() -> bool:
        return any(event.event_type == "LOOP_COMPLETED" and event.entity_id == "shuttle-01" for event in events())

    def events() -> list:
        collected, cursor = [], 0
        while True:
            page = store.replay(session_id, cursor, 500)
            if not page.events:
                return collected
            collected.extend(page.events)
            cursor = page.events[-1].event_sequence

    async def run_until_held():
        for _ in range(400):
            await service.step(service.sessions[session_id])

    client.portal.call(run_until_held)
    snapshot = client.get(f"/api/v2/sessions/{session_id}/snapshot").json()
    assert snapshot["incidents"][0]["vehicle_id"] == "shuttle-01"
    held = client.get(url).json()
    assert held["held_for_incident"] and held["summary"]
    # Answer it the way an operator would, then let the loop finish
    incident = snapshot["incidents"][0]
    option = next(c for c in snapshot["candidates"] if c["action"] == "ESCALATE")
    decided = client.post(f"/api/v2/sessions/{session_id}/commands", json={
        "contract_version": "2.0", "command_id": uuid4().hex, "idempotency_key": uuid4().hex,
        "session_id": session_id, "command_type": "APPROVE_INCIDENT_OPTION", "expected_epoch": snapshot["session_epoch"],
        "expected_revision": incident["aggregate_revision"], "target_type": "INCIDENT", "target_id": incident["incident_id"],
        "payload": {"candidate_id": option["candidate_id"]}, "reason": "Request assistance while safely stopped"})
    assert decided.status_code == 202 and decided.json()["status"] == "APPLIED"
    # A loop at the fixture's speed is thousands of durable ticks; only the clock depends on the speed
    service.sessions[session_id].actor.shuttles[0].cruise_vel *= SPEED_UP
    service.sessions[session_id].actor.incident_due_tick = 10**12

    async def finish_a_loop():
        for _ in range(9000):
            for _ in range(20):
                await service.step(service.sessions[session_id])
            if loop_done():
                return
        raise AssertionError("Shuttle never finished its loop")

    client.portal.call(finish_a_loop)
    outcome = client.get(url).json()
    assert outcome["state"] == "LOOP_COMPLETED" and len(outcome["loops"]) == 1
    assert outcome["service_preserved"] is True
    assert all(stop["times_served"] == 1 for stop in outcome["stops"])
    measured = outcome["loops"][0]
    # Detection now happens during the loop, so its hold is part of the measured time.
    assert 0 < measured["duration_s"] < draft["edited"]["duration_s"]
    assert measured["energy_kwh"] == pytest.approx(draft["edited"]["energy_kwh"], rel=0.5)
    assert outcome["delay_vs_original_s"] is not None and outcome["extra_energy_vs_original_kwh"] is not None
    # The stop dwell and the served stops are in the session's durable record
    served = [event.data["stop_id"] for event in events() if event.event_type == "STOP_SERVED" and event.entity_id == "shuttle-01"]
    assert served[:3] == list(definition.data["route_edit"]["required_stops"])
    json.dumps(outcome)


def test_a_route_restored_from_its_checkpoint_keeps_its_stops(runtime):
    client, store, service, session_id = runtime
    config = get_workflow_definition().routes._fixture
    road = uncurated_road(network().edges, config)
    assert approve(runtime, preview(runtime, waypoint_edge_ids=[road]))["status"] == "APPLIED"
    actor = service.sessions[session_id].actor
    state = json.loads(json.dumps(actor.workflow_state()))
    assert state["stop_names"][ROUTE] == actor.stop_names[ROUTE]
    assert len(state["stop_progress"][ROUTE]) == len(config["required_stops"])
