"""BE-M2-08 independent support, swept collision, bounds and durable replay."""

import asyncio
from dataclasses import replace
import json
import math
from pathlib import Path
from uuid import uuid4

import pytest

from contracts.v2.models import DispatcherCommand, parse_message
from contracts.v2.sessions import CreateSessionRequest
from contracts.v2.spatial import RoadSurface, rotate
from src.energy.model import ENERGY_R1
from src.persistence.training_store import TrainingStore, TrainingStoreError
from src.schemas.shuttle_data import Command, CommandType, ShuttleState
from src.services.spatial_motion import (AABB, RoadPath, SpatialIndex, SpatialUnknown, WorkBudget,
                                         body_bounds, frame_quaternion)
from src.services.spatial_session_actor import MotionLimits, SpatialSessionActor
from src.services.training_scene import validate_snapshot_bindings
from src.services.training_sessions import (SPATIAL_SCENARIO_REVISION, TrainingSessionService, checkpoint)


def single(vehicle="shuttle-05", **kwargs):
    return SpatialSessionActor(vehicle_ids=(vehicle,), **kwargs)


def place(actor, progress, speed=0.):
    shuttle = actor.shuttles[0]
    shuttle.progress = progress
    shuttle.position = actor._route_evaluation(shuttle.route_ID, progress)
    shuttle.curr_vel = speed
    return shuttle


def test_ramp_support_body_offset_and_independent_analytic_orientation():
    actor = single()
    before = actor.generate_snapshot().vehicles[0].spatial
    for _ in range(60):
        actor.tick()
        snapshot = actor.generate_snapshot()
        validate_snapshot_bindings(snapshot, actor.package)
        pose = snapshot.vehicles[0].spatial
        nx, nz = -1/math.sqrt(26), 5/math.sqrt(26)
        support = [pose.position_m[0]-1.3*nx, pose.position_m[1], pose.position_m[2]-1.3*nz]
        assert support[2] == pytest.approx(.2*(support[0]-40), abs=1e-10)
        assert rotate(pose.orientation_xyzw, [1., 0., 0.]) == pytest.approx([nz, 0., -nx])
        assert rotate(pose.orientation_xyzw, [0., 0., 1.]) == pytest.approx([nx, 0., nz])
        assert pose.support_surface_id == "surface-ramp" and pose.deck_id == "deck-ramp"
        assert sum(x*x for x in pose.orientation_xyzw) == pytest.approx(1.)
    assert pose.position_m[2] > before.position_m[2]


def test_3d_arc_length_drives_both_progress_and_energy():
    actor = single()
    shuttle = actor.shuttles[0]
    start, battery = shuttle.progress, shuttle.battery*actor.BATTERY_CAPACITY_KWH/100
    for _ in range(60):
        actor.tick()
    distance = shuttle.progress-start
    final = shuttle.battery*actor.BATTERY_CAPACITY_KWH/100
    expected = distance/1000*ENERGY_R1.drive_kwh_per_km + 60*actor.STEP_S/3600*ENERGY_R1.auxiliary_kw
    assert battery-final == pytest.approx(expected, abs=1e-11)
    assert actor.path.length_m == pytest.approx(40+math.sqrt(20**2+4**2))
    assert distance > abs(shuttle.position.x-50.)
    assert actor.generate_snapshot().routes[0].distance_m == actor.path.length_m


def test_flat_to_ramp_changes_support_at_the_authored_join():
    actor = single("shuttle-01")
    place(actor, 39.)
    surfaces = set()
    for _ in range(50):
        actor.tick()
        pose = actor.generate_snapshot().vehicles[0].spatial
        surfaces.add(pose.support_surface_id)
        support = [a-pose.support_offset_m*b for a, b in zip(pose.position_m, rotate(pose.orientation_xyzw, [0.,0.,1.]))]
        assert support[2] == pytest.approx(max(0., .2*(support[0]-40)), abs=1e-9)
    assert surfaces == {"surface-flat", "surface-ramp"}


def test_acceleration_braking_terminal_dwell_and_standby():
    actor = single()
    previous = 0.
    states = set()
    for _ in range(300):
        actor.tick()
        shuttle = actor.shuttles[0]
        assert shuttle.curr_vel-previous <= actor.limits.acceleration_mps2*actor.STEP_S+1e-9
        assert previous-shuttle.curr_vel <= actor.limits.braking_mps2*actor.STEP_S+1e-4
        previous = shuttle.curr_vel
        states.add(shuttle.state)
    assert {ShuttleState.STOP_DWELL, ShuttleState.DEPOT_STANDBY} <= states
    assert shuttle.progress == actor.path.length_m and shuttle.curr_vel == 0


def test_five_bodies_stop_before_obstruction_and_preserve_separation():
    actor = SpatialSessionActor()
    for _ in range(300):
        actor.tick()
        snapshot = actor.generate_snapshot()
        boxes = [body_bounds(v.spatial.position_m, v.spatial.orientation_xyzw, v.spatial.dimensions_m) for v in snapshot.vehicles]
        assert all(not a.overlaps(b) for i, a in enumerate(boxes) for b in boxes[i+1:])
        assert snapshot.vehicles[3].spatial.position_m[0]+2.4 < 29.  # authored box begins x=29
    assert actor.shuttles[3].state == ShuttleState.BLOCKED
    assert actor.spatial_diagnostics["shuttle-04"]["blockers"] == ["obstruction-01"]
    assert actor.shuttles[4].progress > actor.initial_progress[4]


def test_high_speed_sweep_hits_obstruction_between_clear_endpoints():
    actor = single("shuttle-01", limits=MotionLimits(cruise_mps=1000.))
    budget = WorkBudget(1000)
    boxes = actor._sweep(actor.shuttles[0], 55., budget)
    assert any("obstruction-01" in actor.static_index.query(box, budget) for box in boxes)
    place(actor, 2., 1000.)
    actor.tick()
    assert actor.shuttles[0].progress+2.4 < 29.


def test_fast_follower_cannot_tunnel_through_a_stationary_body():
    actor = SpatialSessionActor(vehicle_ids=("shuttle-01", "shuttle-02"), limits=MotionLimits(cruise_mps=1000.))
    place(actor, 2., 1000.)
    leader = actor.shuttles[1]
    leader.state, leader.dwell_timer = ShuttleState.STOP_DWELL, 100.
    start = leader.progress
    actor.tick()
    assert leader.progress == start
    assert actor.shuttles[0].progress+4.8 < leader.progress
    assert "shuttle-02" in actor.spatial_diagnostics["shuttle-01"]["blockers"]


def test_road_support_and_spawned_vehicle_proxies_are_not_static_blockers():
    actor = single()
    budget = WorkBudget(1000)
    hits = actor.static_index.query(AABB((-100.,-100.,-100.), (100.,100.,100.)), budget)
    assert "obstruction-01" in hits and "building-01" in hits
    assert not any(k.startswith(("road-", "shuttle-", "stop-")) for k in hits)
    actor.tick()
    assert actor.shuttles[0].progress > actor.initial_progress[0]


def test_work_exhaustion_holds_every_body_and_only_uses_auxiliary_energy():
    actor = SpatialSessionActor(limits=MotionLimits(max_work_per_tick=1))
    before = [(s.progress, s.battery) for s in actor.shuttles]
    actor.tick()
    assert actor.current_tick == 1 and actor.spatial_work == 1
    for shuttle, (progress, battery) in zip(actor.shuttles, before):
        assert shuttle.progress == progress and shuttle.curr_vel == 0
        assert (battery-shuttle.battery)*actor.BATTERY_CAPACITY_KWH/100 == pytest.approx(ENERGY_R1.auxiliary_kw*.05/3600)
    assert {c["result"] for c in actor.spatial_diagnostics.values()} == {"UNKNOWN"}
    assert {c["reason"] for c in actor.spatial_diagnostics.values()} == {"SPATIAL_WORK_LIMIT"}


def test_unknown_spatial_work_preserves_dwell_and_charging_semantics():
    actor = SpatialSessionActor(limits=MotionLimits(max_work_per_tick=1))
    dwell, charging = actor.shuttles[:2]
    dwell.state, dwell.dwell_timer = ShuttleState.STOP_DWELL, 1.
    charging.state, charging.curr_vel, charging.battery = ShuttleState.CHARGING, 0., 20.
    battery = charging.battery
    actor.tick()
    assert dwell.state == ShuttleState.STOP_DWELL and dwell.dwell_timer == pytest.approx(.95)
    assert charging.state == ShuttleState.CHARGING and charging.battery > battery
    assert all(check["result"] == "UNKNOWN" for check in actor.spatial_diagnostics.values())


def test_unavailable_reverse_holds_with_an_explicit_unknown():
    actor = single()
    actor.shuttles[0].direction = -1
    start = actor.shuttles[0].progress
    actor.tick()
    assert actor.shuttles[0].progress == start
    assert actor.spatial_diagnostics["shuttle-05"]["reason"] == "REVERSE_NOT_AUTHORED"


def test_seeded_replay_pause_resume_reset_and_detached_client_reads():
    first, second = SpatialSessionActor(), SpatialSessionActor()
    for actor in (first, second):
        for kind, boundary in [(CommandType.PAUSE, 20), (CommandType.RESUME, 30), (CommandType.RESET, 60)]:
            actor.enqueue_command(Command(type=kind, triggered_tick=boundary))
    for _ in range(120):
        first.tick()
        second.tick()
        snapshot = first.generate_snapshot()
        parse_message(snapshot.model_dump(mode="json"))
        snapshot.vehicles[0].spatial.position_m[2] = 1000.
        snapshot.spatial_diagnostics["shuttle-01"]["result"] = "client-camera-and-lod"
        assert first._state_hash() == second._state_hash()
    assert first.session_epoch == 2
    assert first.command_revision == 3 and len(first.command_outcome) == 3


def test_published_slope_stop_trace_matches_the_controller():
    trace_path = Path(__file__).resolve().parents[2] / "docs/milestone_status/M2/be-m2-08-trace.json"
    trace = json.loads(trace_path.read_bytes())
    actor = SpatialSessionActor()
    assert trace["spatial"] == actor.reference.model_dump()
    assert trace["scenario_revision"] == actor.scenario_revision
    for _ in range(trace["verified_steps"]):
        actor.tick()
    assert actor._state_hash() == trace["samples"][-1]["state_hash"]
    assert trace["samples"][-1]["vehicles"][4]["state"] == "STANDBY"


def surface(points):
    return RoadSurface(surface_id="s", entity_id="road", lane_id="l", deck_id="d", directed_edge_id="e",
                       width_m=6., path_samples_m=points, normal=[0.,0.,1.], permitted_next_surface_ids=[], clearance_m=5.)


def test_bend_sweep_includes_rotation_and_does_not_cut_the_corner():
    path = RoadPath([surface([[0.,0.,0.],[10.,0.,0.],[10.,10.,0.]])])
    boxes = path.sweep(8., 12., [4.8,2.,2.6], 1.3, .02, WorkBudget(100))
    index = SpatialIndex([("corner", AABB((11.5,-.1,1.), (11.6,.1,1.1)))])
    assert any(index.query(box, WorkBudget(100)) for box in boxes)
    assert path.pose(12., 1.3)[0] == pytest.approx([10.,2.,1.3])


def test_sweep_piece_limit_is_unknown_instead_of_unchecked_motion():
    path = RoadPath([surface([[float(i), 0., 0.] for i in range(40)])])
    with pytest.raises(SpatialUnknown, match="SWEEP_PIECE_LIMIT"):
        path.sweep(0., 39., [4.,2.,2.], 1., .02, WorkBudget(1000))


def test_3d_broad_phase_preserves_height_and_rotated_extents():
    index = SpatialIndex([("above", body_bounds([0.,0.,10.], [0.,0.,0.,1.], [2.,2.,2.]))])
    assert not index.query(body_bounds([0.,0.,1.3], [0.,0.,0.,1.], [4.8,2.,2.6]), WorkBudget(100))
    q = frame_quaternion([0.,1.,0.], [0.,0.,1.])
    bounds = body_bounds([0.,0.,1.3], q, [4.8,2.,2.6])
    assert bounds.high == pytest.approx([1.,2.4,2.6])


def test_crossing_sweeps_overlap_even_when_endpoints_are_separate():
    first = RoadPath([surface([[-10.,0.,0.],[10.,0.,0.]])])
    second = RoadPath([surface([[0.,-10.,0.],[0.,10.,0.]])])
    a = first.sweep(0.,20.,[4.,2.,2.],1.,.02,WorkBudget(100))
    b = second.sweep(0.,20.,[4.,2.,2.],1.,.02,WorkBudget(100))
    assert any(x.overlaps(y) for x in a for y in b)


@pytest.mark.parametrize("change", [{"max_work_per_tick": 0}, {"max_work_per_tick": True},
                                    {"search_iterations": 17}, {"cruise_mps": math.inf}])
def test_invalid_model_limits_rejected_before_ticking(change):
    with pytest.raises(ValueError):
        replace(MotionLimits(), **change)


def test_no_unauthored_surface_transition():
    first = surface([[0.,0.,0.],[10.,0.,0.]])
    second = surface([[10.,0.,0.],[20.,0.,0.]]).model_copy(update={"surface_id": "next"})
    with pytest.raises(ValueError, match="INVALID_SURFACE_TRANSITION"):
        RoadPath([first, second])


def test_private_checkpoint_roundtrip_and_geometry_pins():
    actor = SpatialSessionActor()
    for _ in range(40):
        actor.tick()
    snapshot = actor.generate_snapshot()
    state = json.loads(json.dumps(checkpoint(actor)))
    restored = TrainingSessionService._restore_session(snapshot, state).actor
    assert restored._state_hash() == actor._state_hash()
    for _ in range(20):
        actor.tick()
        restored.tick()
        assert restored._state_hash() == actor._state_hash()
    state["spatial"]["spatial_revision"] = "wrong-revision"
    with pytest.raises(TrainingStoreError, match="Spatial geometry"):
        TrainingSessionService._restore_session(snapshot, state)


@pytest.mark.parametrize("mutation", ["roster", "direction", "progress", "work", "diagnostics"])
def test_corrupt_spatial_checkpoints_are_rejected(mutation):
    actor = SpatialSessionActor()
    snapshot, state = actor.generate_snapshot(), checkpoint(actor)
    if mutation == "roster": state["shuttles"][0]["ID"] = 5
    if mutation == "direction": state["shuttles"][0]["direction"] = -1
    if mutation == "progress": state["shuttles"][0]["progress"] = 1000.
    if mutation == "work": state["spatial_work"] = 100000
    if mutation == "diagnostics": state["spatial_diagnostics"]["shuttle-01"]["blockers"] = ["unknown"]
    with pytest.raises((ValueError, TrainingStoreError)):
        TrainingSessionService._restore_session(snapshot, state)


def command(snapshot, kind, key):
    return DispatcherCommand(contract_version="2.1", command_id=str(uuid4()), idempotency_key=key,
                             session_id=snapshot.session_id, command_type=kind, expected_epoch=snapshot.session_epoch,
                             expected_revision=snapshot.session.aggregate_revision, target_type="SESSION",
                             target_id=snapshot.session_id, payload={}, reason=None)


def test_spatial_admission_commits_commands_once_and_recovers_paused(tmp_path, monkeypatch):
    async def manual_clock(self, session):
        await asyncio.Event().wait()
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)
    path = tmp_path / "spatial.sqlite"

    async def exercise(store):
        service = TrainingSessionService(store)
        request = CreateSessionRequest(scenario_revision_id=SPATIAL_SCENARIO_REVISION, seed=1000)
        created = await service.create(request, "operator", "attempt")
        assert created.spatial and created.session.status == "READY"
        assert (await service.create(request, "operator", "attempt")).snapshot_id == created.snapshot_id
        start = command(created, "START", "start-once")
        assert (await service.submit("operator", start)).status == "APPLIED"
        assert (await service.submit("operator", start)).status == "APPLIED"
        live = service.sessions[created.session_id]
        for _ in range(20):
            await service.step(live)
        visible = await service.snapshot(created.session_id)
        assert visible.tick == 20 and visible.vehicles[4].position.z_m > created.vehicles[4].position.z_m
        # A failed durable commit never installs a moved actor.
        await service.step(live)
        installed = live.actor._state_hash()
        original = store.commit_snapshot
        def fail(*args, **kwargs):
            raise TrainingStoreError("WRITE_FAILED", "injected failure")
        monkeypatch.setattr(store, "commit_snapshot", fail)
        with pytest.raises(TrainingStoreError):
            await service.step(live)
        assert live.actor._state_hash() == installed
        monkeypatch.setattr(store, "commit_snapshot", original)
        await service.close()
        return visible

    with TrainingStore(path) as store:
        committed = asyncio.run(exercise(store))
    with TrainingStore(path) as store:
        async def recover():
            service = TrainingSessionService(store)
            try:
                recovered = await service.snapshot(committed.session_id)
                assert recovered.session.status == "PAUSED"
                assert recovered.session_epoch == committed.session_epoch+1
                assert recovered.spatial == committed.spatial
                assert recovered.vehicles == committed.vehicles
                assert isinstance(service.sessions[committed.session_id].actor, SpatialSessionActor)
                resume = command(recovered, "RESUME", "resume-once")
                assert (await service.submit("operator", resume)).status == "APPLIED"
                for _ in range(2):
                    await service.step(service.sessions[committed.session_id])
                assert (await service.snapshot(committed.session_id)).tick == committed.tick+2
            finally:
                await service.close()
        asyncio.run(recover())
