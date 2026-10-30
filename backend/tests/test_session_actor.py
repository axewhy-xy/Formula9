"""Regressions for the PR #78 fleet actor review and its v2 handoff."""

import importlib
import math

import pytest

from contracts.v2.models import FullSnapshot, parse_message
from src.persistence.training_store import TrainingStore
from src.schemas.shuttle_data import Command, CommandType, Shuttle, ShuttleState, Vector2D
from src.energy.model import ENERGY_R1
from src.services.asset_loader import MAX_ASSET_BYTES, MAX_WAYPOINTS, load_routes_from_json
from src.services.session_actor import SessionActor


def route(identifier=0, points=((0, 0), (1000, 0))):
    return {"route_ID": identifier, "waypoints": [Vector2D(x=x, y=y) for x, y in points],
            "total_length": 1000.0}


def actor(routes=None, count=1):
    return SessionActor(seed=1000, route=routes or [route()], shuttle_cnt=count)


def vehicle(owner, identifier=0, route_id=0, progress=0.0, speed=20.0,
            state=ShuttleState.ROUTE_LOOP, battery=100.0, dwell=60.0):
    return Shuttle(ID=identifier, route_ID=route_id, state=state, progress=progress,
                   curr_vel=speed, cruise_vel=speed, battery=battery, dwell_timer=dwell,
                   position=owner._route_evaluation(route_id, progress))


def test_normal_backend_package_imports():
    assert importlib.import_module("src.services.session_actor").SessionActor is SessionActor
    assert importlib.import_module("src.services.asset_loader").load_routes_from_json is load_routes_from_json


@pytest.mark.parametrize("speed,distance", [(20.0, 1.0), (0.0, 0.0)])
def test_velocity_is_integrated_in_seconds(speed, distance):
    session = actor()
    session.shuttles[0].curr_vel = speed
    session.tick()
    assert session.shuttles[0].progress == pytest.approx(distance)
    assert session.timestamp_second == 0.05


def test_high_speed_follower_stops_before_stationary_leader():
    session = actor(count=0)
    session.shuttles = [vehicle(session, speed=400),
                        vehicle(session, 1, progress=18, speed=0, state=ShuttleState.STOP_DWELL)]
    session.tick()
    follower, leader = session.shuttles
    assert follower.progress <= leader.progress - session.HEADWAY_M
    assert follower.state == ShuttleState.BLOCKED
    assert leader.state == ShuttleState.STOP_DWELL


def test_swept_crossing_detects_collision_between_distant_endpoints():
    session = actor([route(0, ((-30, 0), (30, 0))), route(1, ((0, -30), (0, 30)))], count=0)
    session.shuttles = [vehicle(session, 0, 0, progress=10, speed=800),
                        vehicle(session, 1, 1, progress=10, speed=800)]
    session.tick()
    winner, yielding = session.shuttles
    assert winner.progress == 50
    assert yielding.progress == 10
    assert yielding.state == ShuttleState.BLOCKED
    assert math.hypot(winner.position.x - yielding.position.x,
                      winner.position.y - yielding.position.y) >= session.SEPARATION_M


def test_swept_check_includes_bends_in_the_route():
    session = actor([route(0, ((0, 0), (20, 0), (20, 20))),
                     route(1, ((20, -20), (20, 30)))], count=0)
    session.shuttles = [vehicle(session, speed=800),
                        vehicle(session, 1, 1, progress=20, speed=0, state=ShuttleState.DEPOT_STANDBY)]
    session.tick()
    assert session.shuttles[0].progress == 0
    assert session.shuttles[0].state == ShuttleState.BLOCKED


def test_collision_resolution_rechecks_paths_after_a_peer_stops():
    session = actor([route(0, ((-30, 0), (30, 0))), route(1, ((0, -30), (0, 100)))], count=0)
    session.shuttles = [vehicle(session, 0, 0, progress=10, speed=800),
                        vehicle(session, 1, 1, progress=22, speed=800)]
    session.tick()
    # Stopping only the higher ID would leave it directly in the winner's swept path.
    assert [s.progress for s in session.shuttles] == [10, 22]
    assert all(s.state == ShuttleState.BLOCKED for s in session.shuttles)


def test_exact_overlap_is_detected_instead_of_ignored():
    session = actor(count=0)
    session.shuttles = [vehicle(session, 0), vehicle(session, 1)]
    session.tick()
    assert all(s.state == ShuttleState.BLOCKED and s.progress == 0 for s in session.shuttles)


@pytest.mark.parametrize("state", [ShuttleState.STOP_DWELL, ShuttleState.CHARGING,
                                  ShuttleState.DEPOT_STANDBY])
def test_safety_does_not_overwrite_stationary_operational_states(state):
    session = actor(count=0)
    session.shuttles = [vehicle(session, 0, progress=0),
                        vehicle(session, 1, progress=5, speed=0, state=state, battery=40)]
    session.tick()
    assert session.shuttles[0].state == ShuttleState.BLOCKED
    assert session.shuttles[1].state == state


def test_charging_is_not_starved_by_nearby_vehicle():
    session = actor(count=0)
    session.shuttles = [vehicle(session, 0, speed=0, state=ShuttleState.DEPOT_STANDBY, battery=20),
                        vehicle(session, 1, progress=10, speed=0, state=ShuttleState.STOP_DWELL)]
    for _ in range(10):
        session.tick()
    assert session.shuttles[0].state == ShuttleState.CHARGING
    assert session.shuttles[0].battery > 20
    snapshot = session.generate_snapshot()
    assert parse_message(snapshot.model_dump(mode="json")).vehicles[0].service_state == "CHARGING"
    assert snapshot.chargers[0].occupied_vehicle_ids == [snapshot.vehicles[0].vehicle_id]


def test_farther_leader_does_not_release_a_nearer_restriction():
    session = actor(count=0)
    session.shuttles = [vehicle(session),
                        vehicle(session, 1, progress=10, speed=0, state=ShuttleState.STOP_DWELL),
                        vehicle(session, 2, progress=50, speed=0, state=ShuttleState.STOP_DWELL)]
    session.tick()
    assert session.shuttles[0].state == ShuttleState.BLOCKED
    assert session.shuttles[0].progress == 0


def test_cross_route_block_clears_without_a_same_route_peer():
    session = actor([route(0), route(1, ((100, 0), (100, 100)))], count=0)
    session.shuttles = [vehicle(session, 0, 0, progress=95, speed=0, state=ShuttleState.STOP_DWELL),
                        vehicle(session, 1, 1)]
    session.tick()
    assert session.shuttles[1].state == ShuttleState.BLOCKED
    session.shuttles[0].progress = 0
    session.tick()
    assert session.shuttles[1].state == ShuttleState.ROUTE_LOOP
    assert session.shuttles[1].progress == 1
    assert session.shuttles[1].curr_vel == 20


@pytest.mark.parametrize("state", [ShuttleState.STOP_DWELL, ShuttleState.CHARGING, ShuttleState.BLOCKED])
def test_resume_restores_cruising_velocity(state):
    session = actor()
    shuttle = session.shuttles[0]
    shuttle.state = state
    shuttle.curr_vel = 0
    shuttle.dwell_timer = 0.05
    shuttle.battery = 59.9999 if state == ShuttleState.CHARGING else 100
    session.tick()
    assert shuttle.state == ShuttleState.ROUTE_LOOP
    assert shuttle.curr_vel == 20
    progress = shuttle.progress
    session.tick()
    assert shuttle.progress == pytest.approx(progress + 1)


def test_open_route_dwells_at_endpoint_then_drives_back_without_teleport():
    session = actor([route(0, ((0, 0), (100, 0)))])
    shuttle = session.shuttles[0]
    shuttle.progress = 99
    session.tick()
    assert shuttle.progress == 100 and shuttle.position.x == 100
    assert shuttle.state == ShuttleState.STOP_DWELL
    for _ in range(40):
        session.tick()
        assert shuttle.position.x == 100
    assert shuttle.direction == -1 and shuttle.curr_vel == 20
    session.tick()
    assert shuttle.progress == 99 and shuttle.position.x == 99


@pytest.mark.parametrize("route_count,open_index,count", [
    (1, 0, 2), (2, 0, 3), (2, 1, 4), (3, 0, 4), (3, 1, 5),
])
def test_shared_open_route_is_rejected_before_simulation(route_count, open_index, count):
    routes = load_routes_from_json()[:route_count]
    identifier = routes[open_index]["route_ID"]
    routes[open_index] = route(identifier, ((0, 0), (100, 0)))
    with pytest.raises(ValueError, match=f"Open route {identifier!r} cannot be assigned to multiple shuttles"):
        actor(routes, count=count)


@pytest.mark.parametrize("count", [3, 4])
def test_open_routes_are_allowed_when_only_closed_routes_are_shared(count):
    routes = load_routes_from_json()
    routes[1] = route("open-middle", ((300, 0), (400, 0)))
    routes[2] = route("open-last", ((600, 0), (700, 0)))
    session = actor(routes, count=count)
    reversed_ids = set()
    for _ in range(400):
        session.tick()
        reversed_ids.update(s.ID for s in session.shuttles if s.direction == -1)
        assert all(s.state in {ShuttleState.ROUTE_LOOP, ShuttleState.STOP_DWELL}
                   for s in session.shuttles)
    assert reversed_ids == {1, 2}


def test_closed_route_wraps_only_after_endpoint_dwell():
    session = actor([route(0, ((0, 0), (25, 0), (25, 25), (0, 25), (0, 0)))])
    shuttle = session.shuttles[0]
    shuttle.progress = 99
    session.tick()
    assert shuttle.progress == 100 and shuttle.position == Vector2D(x=0, y=0)
    for _ in range(40):
        session.tick()
    assert shuttle.progress == 0 and shuttle.position == Vector2D(x=0, y=0)
    session.tick()
    assert shuttle.progress == 1


def test_closed_route_headway_accounts_for_wrap():
    session = actor([route(0, ((0, 0), (25, 0), (25, 25), (0, 25), (0, 0)))], count=0)
    session.shuttles = [vehicle(session, 0, progress=95),
                        vehicle(session, 1, progress=5, speed=0, state=ShuttleState.STOP_DWELL)]
    session.tick()
    assert session.shuttles[0].progress == 95
    assert session.shuttles[0].state == ShuttleState.BLOCKED


def test_pause_freezes_simulation_clock_but_future_resume_is_processed():
    session = actor()
    session.enqueue_command(Command(type=CommandType.PAUSE, triggered_tick=2))
    session.enqueue_command(Command(type=CommandType.RESUME, triggered_tick=5))
    emissions = []
    for _ in range(4):
        session.tick()
        emissions.append(session.should_emit_snapshot())
    assert emissions == [False, True, False, True]
    assert session.command_tick == 4 and session.current_tick == 1
    assert session.generate_snapshot().simulation_time_s == 0.05
    assert session.shuttles[0].progress == 1
    session.tick()
    assert not session.is_paused and session.current_tick == 2
    assert session.timestamp_second == 0.1


def test_reset_preserves_due_batch_outcomes_and_future_command_schedule():
    session = actor()
    for kind, when in [(CommandType.RESET, 2), (CommandType.PAUSE, 2), (CommandType.RESUME, 4)]:
        session.enqueue_command(Command(type=kind, triggered_tick=when))
    session.tick()
    session.tick()
    assert session.current_tick == 0 and session.timestamp_second == 0
    assert session.is_paused and session.session_epoch == 2
    assert [c.processing_tick for c in session.command_outcome] == [2, 2]
    assert [c.command_type for c in session.command_outcome] == [CommandType.RESET, CommandType.PAUSE]
    session.tick()
    session.tick()
    assert session.command_tick == 4 and session.current_tick == 1
    assert not session.is_paused and not session.command_queue
    assert session.command_outcome[-1].processing_tick == 4


@pytest.mark.parametrize("field,value", [("curr_vel", 0), ("cruise_vel", 5),
                                        ("dwell_timer", 1), ("direction", -1)])
def test_hash_detects_controller_state_differences(field, value):
    first, second = actor(), actor()
    setattr(second.shuttles[0], field, value)
    assert first.generate_snapshot().state_hash != second.generate_snapshot().state_hash


@pytest.mark.parametrize("change", ["pause", "queue", "rng"])
def test_hash_detects_pause_queue_and_rng_differences(change):
    first, second = actor(), actor()
    if change == "pause":
        second.is_paused = True
    elif change == "queue":
        second.enqueue_command(Command(type=CommandType.PAUSE, triggered_tick=10))
    else:
        second.rng.random()
    assert first.generate_snapshot().state_hash != second.generate_snapshot().state_hash


def test_default_fleet_runs_beyond_crash_tick_and_replays_deterministically():
    routes = load_routes_from_json()
    first, second = actor(routes, 5), actor(routes, 5)
    laps = {s.ID: 0 for s in first.shuttles}
    for _ in range(2400):
        before = {s.ID: s.progress for s in first.shuttles}
        first.tick()
        second.shuttles.reverse()
        second.tick()
        assert first.generate_snapshot().state_hash == second.generate_snapshot().state_hash
        for i, shuttle in enumerate(first.shuttles):
            if shuttle.progress < before[shuttle.ID]:
                laps[shuttle.ID] += 1
            assert shuttle.state in {ShuttleState.ROUTE_LOOP, ShuttleState.STOP_DWELL}
            for other in first.shuttles[i + 1:]:
                assert math.hypot(shuttle.position.x - other.position.x,
                                  shuttle.position.y - other.position.y) >= first.SEPARATION_M - 1e-9
    assert first.current_tick == 2400
    assert min(laps.values()) >= 2
    assert min(s.battery for s in first.shuttles) > 50


def test_snapshot_is_canonical_and_owns_no_actor_buffers():
    session = actor()
    snapshot = session.generate_snapshot()
    restored = FullSnapshot.model_validate_json(snapshot.model_dump_json())
    assert parse_message(snapshot.model_dump(mode="json")).message_type == "FULL_SNAPSHOT"
    assert restored.vehicles[0].battery_kwh == 40
    snapshot.vehicles[0].position.x_m = 999
    snapshot.vehicles[0].battery_kwh = 0
    assert session.shuttles[0].position.x == 0
    assert session.generate_snapshot().vehicles[0].battery_kwh == 40
    assert session.command_tick == 0


def test_snapshot_can_be_created_and_committed_in_existing_store(tmp_path):
    session = actor()
    with TrainingStore(tmp_path / "fleet.sqlite") as store:
        store.create_session(session.generate_snapshot())
        session.tick()
        store.commit_snapshot(session.generate_snapshot())
        saved = store.snapshot(session.session_id)
        assert saved.tick == 1 and saved.vehicles[0].position.x_m == 1
        assert saved.model_extra["state_hash"] == session.generate_snapshot().state_hash


def test_real_route_ids_survive_reordering_and_asset_mutation():
    routes = load_routes_from_json()
    for item, identifier in zip(routes, [5, 3, "route-z"]):
        item["route_ID"] = identifier
    session = actor(routes, 5)
    routes[0]["waypoints"][0].x = -999
    assert [s.route_ID for s in session.shuttles] == [5, 3, "route-z", 5, 3]
    assert session.shuttles[0].position.x == 0
    snapshot = session.generate_snapshot()
    assert {v.route_id for v in snapshot.vehicles} == {r.route_id for r in snapshot.routes}


def test_maximum_length_canonical_route_id_keeps_valid_edge_references():
    identifier = "r" * 128
    session = actor([route(identifier)])
    snapshot = parse_message(session.generate_snapshot().model_dump(mode="json"))
    assert snapshot.vehicles[0].route_id == identifier
    assert all(len(edge) <= 128 for edge in snapshot.routes[0].directed_edge_ids)


def test_initial_endpoint_occupancy_starts_in_dwell():
    session = actor([route(0), route(1, ((0, 100), (20, 100)))], count=2)
    assert session.shuttles[1].state == ShuttleState.STOP_DWELL
    assert session.shuttles[1].curr_vel == 0
    assert session.shuttles[1].dwell_timer == session.DWELL_S


def test_default_asset_path_is_independent_of_working_directory(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    routes = load_routes_from_json()
    assert [r["route_ID"] for r in routes] == [0, 1, 2]
    assert routes[1]["total_length"] == pytest.approx(200 + 2 * math.hypot(100, 200))
    assert routes[2]["total_length"] == 800
    assert all(r["waypoints"][0] == r["waypoints"][-1] for r in routes)


def test_missing_requested_asset_does_not_substitute_a_different_scenario(tmp_path):
    with pytest.raises(FileNotFoundError):
        load_routes_from_json(tmp_path / "missing.json")


@pytest.mark.parametrize("routes", [[], [route(0, ((0, 0),))],
                                     [route(0, ((0, 0), (0, 0)))], [route(0), route("0")]])
def test_invalid_route_assets_fail_before_first_tick(routes):
    with pytest.raises(ValueError):
        SessionActor(seed=1, route=routes)


def test_tick_does_no_filesystem_io(monkeypatch):
    session = actor()

    def fail(*args, **kwargs):
        raise AssertionError("Filesystem access inside tick")

    monkeypatch.setattr("builtins.open", fail)
    monkeypatch.setattr("pathlib.Path.open", fail)
    monkeypatch.setattr("json.load", fail)
    session.tick()
    assert session.current_tick == 1


@pytest.mark.parametrize("routes", [
    [route(i) for i in range(4)],
    [route(points=tuple((i, 0) for i in range(MAX_WAYPOINTS + 1)))],
    [route(points=((0, 0), (1_000_001, 0)))],
    [{}], [None],
])
def test_unbounded_or_malformed_geometry_fails_before_first_tick(routes):
    with pytest.raises(ValueError):
        SessionActor(seed=1, route=routes)


def test_oversized_asset_is_rejected_before_json_decode(tmp_path):
    path = tmp_path / "oversized.json"
    path.write_bytes(b" " * (MAX_ASSET_BYTES + 1))
    with pytest.raises(ValueError, match="bytes"):
        load_routes_from_json(path)


@pytest.mark.parametrize("keyword,value", [("seed", True), ("seed", 1.5), ("shuttle_cnt", True)])
def test_invalid_actor_configuration_fails_at_creation(keyword, value):
    arguments = {"seed": 1, "route": [route()], keyword: value}
    with pytest.raises(ValueError):
        SessionActor(**arguments)


def test_control_queue_is_bounded_without_discarding_accepted_controls():
    session = actor()
    command = Command(type=CommandType.PAUSE, triggered_tick=10)
    for _ in range(session.MAX_PENDING_COMMANDS):
        session.enqueue_command(command)
    with pytest.raises(ValueError, match="queue is full"):
        session.enqueue_command(command)
    for _ in range(10):
        session.tick()
    assert session.command_revision == session.MAX_PENDING_COMMANDS
    assert len(session.command_outcome) == session.MAX_PENDING_COMMANDS
    assert not session.command_queue


def test_retained_outcomes_are_bounded_but_revision_and_reset_history_are_monotonic():
    session = actor()
    for _ in range(session.MAX_RETAINED_OUTCOMES + 3):
        session.enqueue_command(Command(type=CommandType.PAUSE, triggered_tick=0))
        session.tick()
    session.enqueue_command(Command(type=CommandType.RESET, triggered_tick=0))
    session.tick()
    snapshot = session.generate_snapshot()
    assert len(session.command_outcome) == session.MAX_RETAINED_OUTCOMES
    assert snapshot.session.aggregate_revision == session.MAX_RETAINED_OUTCOMES + 4
    assert snapshot.session_epoch == 2
    assert session.command_outcome[-1].command_type == CommandType.RESET


def test_charging_deducts_auxiliary_use_and_releases_only_at_net_target():
    session = actor()
    shuttle = session.shuttles[0]
    shuttle.state = ShuttleState.CHARGING
    shuttle.curr_vel = 0
    net_kw = session.CHARGER_POWER_KW * ENERGY_R1.charge_efficiency - ENERGY_R1.auxiliary_kw
    gain_pct = net_kw * session.STEP_S / 3600 / session.BATTERY_CAPACITY_KWH * 100
    shuttle.battery = 60 - gain_pct - 0.00001
    session.tick()
    assert shuttle.battery == pytest.approx(60 - 0.00001)
    assert shuttle.state == ShuttleState.CHARGING
    session.tick()
    assert shuttle.state == ShuttleState.ROUTE_LOOP
    assert shuttle.battery >= 60


def test_manifest_identity_changes_with_geometry_and_scenario_inputs():
    first = actor()
    moved = actor([route(points=((0, 1), (1000, 1)))])
    new_seed = SessionActor(seed=1001, route=[route()], shuttle_cnt=1)
    assert first.scene_revision != moved.scene_revision
    assert first.scenario_revision != moved.scenario_revision
    assert first.scene_revision == new_seed.scene_revision
    assert first.scenario_revision != new_seed.scenario_revision
    assert first.generate_snapshot().scenario.revision_id == first.scenario_revision
