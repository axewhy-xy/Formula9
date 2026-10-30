"""Authoritative M2 3D kinematics on the verified shared scene (no GPU)."""

from __future__ import annotations

from dataclasses import asdict, dataclass
from copy import deepcopy
import hashlib
import json
import math

from contracts.v2.spatial import rotate
from src.schemas.shuttle_data import FleetSnapshot, ShuttleState, Vector2D
from .session_actor import SessionActor
from .spatial_motion import (RoadPath, SpatialIndex, SpatialUnknown, WorkBudget,
                             body_bounds, envelope)
from .training_scene import TrainingScenePackage, get_training_scene, validate_snapshot_bindings


@dataclass(frozen=True)
class MotionLimits:
    revision: str = "road-controller-r1"
    cruise_mps: float = 4.
    acceleration_mps2: float = 2.
    braking_mps2: float = 4.
    collision_margin_m: float = .02
    stopping_gap_m: float = .10
    max_work_per_tick: int = 4096
    search_iterations: int = 12

    def __post_init__(self):
        values = (self.cruise_mps, self.acceleration_mps2, self.braking_mps2,
                  self.collision_margin_m, self.stopping_gap_m)
        if any(not math.isfinite(v) or v <= 0 for v in values):
            raise ValueError("INVALID_MOTION_LIMITS")
        if (type(self.max_work_per_tick) is not int or not 1 <= self.max_work_per_tick <= 16384
                or type(self.search_iterations) is not int or not 1 <= self.search_iterations <= 16
                or self.revision != "road-controller-r1"):
            raise ValueError("INVALID_SPATIAL_WORK_BOUND")


class SpatialSessionActor(SessionActor):
    """Reuse the 50 ms clock, controls, dwell, battery and commit boundary.

    The M2 package's one directed strip terminates in a dwell/standby, rather
    than inventing a reverse lane. Five bodies start at their authored poses.
    Every required unknown holds the entire fleet for that boundary.
    """

    def __init__(self, seed: int = 1000, *, session_id="fleet-3d-session",
                 package: TrainingScenePackage | None = None,
                 surface_ids=("surface-flat", "surface-ramp"),
                 vehicle_ids=tuple(f"shuttle-{i:02}" for i in range(1, 6)),
                 limits: MotionLimits = MotionLimits()):
        self.package = package or get_training_scene()
        self.reference = self.package.reference()
        manifest = self.package.manifest
        surfaces = {s.surface_id: s for s in manifest.road_surfaces}
        if (not 1 <= len(vehicle_ids) <= 5 or len(set(vehicle_ids)) != len(vehicle_ids)
                or not 1 <= len(surface_ids) <= 64 or len(set(surface_ids)) != len(surface_ids)):
            raise ValueError("INVALID_SPATIAL_FIXTURE_BINDINGS")
        self.path = RoadPath([surfaces[key] for key in surface_ids])
        self.surface_ids = tuple(surface_ids)
        self.edge_ids = tuple(surfaces[key].directed_edge_id for key in surface_ids)
        self.edge_paths = tuple([{"x_m": x, "y_m": y, "z_m": z} for x, y, z in surfaces[key].path_samples_m]
                                for key in surface_ids)
        entities = {e.entity_id: e for e in manifest.entities}
        self.bodies = tuple(entities[key] for key in vehicle_ids)
        self.assets = {a.asset_id: a for a in manifest.assets}
        self.proxies = {p.proxy_id: p for p in manifest.proxies}
        self.limits = limits
        self.initial_progress = []
        for body in self.bodies:
            if body.kind != "SHUTTLE":
                raise ValueError("INVALID_SHUTTLE_BINDING")
            normal = rotate(body.orientation_xyzw, [0., 0., 1.])
            support = [p-body.dimensions_m[2]/2*n for p, n in zip(body.position_m, normal)]
            progress = self.path.project(support)
            position, orientation, _ = self.path.pose(progress, body.dimensions_m[2]/2)
            if (math.dist(position, body.position_m) > 1e-5
                    or abs(sum(a*b for a, b in zip(orientation, body.orientation_xyzw))) < 1-1e-6
                    or any(body.dimensions_m[1] + 2*limits.collision_margin_m > s.width_m
                           or body.dimensions_m[2] + limits.collision_margin_m > s.clearance_m
                           for s in self.path.segments)):
                raise ValueError("UNSUPPORTED_BODY_OR_SURFACE_FIT")
            self.initial_progress.append(progress)
        # Moving shuttle proxies must never remain as static ghosts at spawn.
        self.static_index = SpatialIndex([
            (p.entity_id, body_bounds(p.position_m, p.orientation_xyzw, p.dimensions_m))
            for p in manifest.proxies if p.classification == "BLOCKING" and entities[p.entity_id].kind != "SHUTTLE"
        ])
        route = {"route_ID": "route-m2-road-3d", "total_length": self.path.length_m,
                 "waypoints": [Vector2D(x=s.start[0], y=s.start[1]) for s in self.path.segments]
                              + [Vector2D(x=self.path.segments[-1].end[0], y=self.path.segments[-1].end[1])]}
        super().__init__(seed=seed, route=[route], shuttle_cnt=len(self.bodies), session_id=session_id)
        self.scene_revision = self.reference.scene_revision
        blueprint = {"base": self.scenario_revision, "spatial": self.reference.model_dump(),
                     "limits": asdict(limits), "surfaces": self.surface_ids,
                     "vehicles": vehicle_ids, "initial_progress_m": self.initial_progress}
        digest = hashlib.sha256(json.dumps(blueprint, sort_keys=True).encode()).hexdigest()[:24]
        self.scenario_revision = f"road-scenario-{digest}"
        validate_snapshot_bindings(self.generate_snapshot(), self.package)

    def _validated_routes(self, routes):
        # Already compiled from validated 3D surfaces; retain their arc length.
        return routes

    def _validate_assignments(self):
        # Directed terminal routes do not reverse. Swept bodies handle sharing.
        pass

    def _reset_simulation_state(self):
        super()._reset_simulation_state()
        self.spatial_diagnostics = {body.entity_id: {"result": "CLEAR", "blockers": []} for body in self.bodies}
        self.spatial_work = 0
        for shuttle, progress in zip(self.shuttles, self.initial_progress):
            shuttle.progress = progress
            shuttle.position = self._route_evaluation(shuttle.route_ID, progress)
            shuttle.curr_vel = 0.
            shuttle.cruise_vel = self.limits.cruise_mps
            shuttle.state = ShuttleState.ROUTE_LOOP
            shuttle.dwell_timer = 0.

    def _route_evaluation(self, route_ID, progress):
        position, _, _ = self.path.pose(max(0., min(self.path.length_m, progress)), 0.)
        return Vector2D(x=position[0], y=position[1])

    def _resume_route(self, shuttle):
        shuttle.state = ShuttleState.ROUTE_LOOP

    def _prepare_motion(self, shuttle):
        if shuttle.direction != 1:
            # No reverse topology has been authored for this M2 strip.
            return shuttle.progress
        if shuttle.state == ShuttleState.STOP_DWELL and shuttle.progress == self.path.length_m:
            shuttle.curr_vel = 0.
            shuttle.dwell_timer = max(0., shuttle.dwell_timer-self.fixed_DT)
            if shuttle.dwell_timer <= 1e-9:
                shuttle.state = ShuttleState.DEPOT_STANDBY
            return shuttle.progress
        if shuttle.state in {ShuttleState.ROUTE_LOOP, ShuttleState.BLOCKED} and shuttle.battery >= 30:
            shuttle.curr_vel = min(shuttle.cruise_vel, shuttle.curr_vel+self.limits.acceleration_mps2*self.fixed_DT)
            return min(self.path.length_m, shuttle.progress+shuttle.curr_vel*self.fixed_DT)
        return super()._prepare_motion(shuttle)

    def _sweep(self, shuttle, end, budget):
        body = self.bodies[shuttle.ID]
        if shuttle.direction != 1:
            raise SpatialUnknown("REVERSE_NOT_AUTHORED")
        return self.path.sweep(shuttle.progress, end, body.dimensions_m, body.dimensions_m[2]/2,
                               self.limits.collision_margin_m, budget)

    def _blockers(self, shuttle, end, current_index, budget):
        found = set()
        own_id = self.bodies[shuttle.ID].entity_id
        for box in self._sweep(shuttle, end, budget):
            found.update(self.static_index.query(box, budget))
            found.update(k for k in current_index.query(box, budget) if k != own_id)
        return sorted(found)

    def _resolve_motion(self, proposed):
        budget = WorkBudget(self.limits.max_work_per_tick)
        blocked = set()
        self.spatial_diagnostics = {b.entity_id: {"result": "CLEAR", "blockers": []} for b in self.bodies}
        try:
            # Occupants at committed poses are conservative braking targets.
            current = SpatialIndex([(self.bodies[s.ID].entity_id, envelope(self._sweep(s, s.progress, budget)))
                                    for s in self.shuttles])
            for shuttle in self.shuttles:
                if proposed[shuttle.ID] == shuttle.progress:
                    continue
                speed, decel, dt = shuttle.curr_vel, self.limits.braking_mps2, self.fixed_DT
                horizon = min(self.path.length_m, shuttle.progress + speed*dt + speed*speed/(2*decel)
                              + self.limits.stopping_gap_m)
                blockers = self._blockers(shuttle, horizon, current, budget)
                # The terminal is also a braking target.
                clear = horizon-shuttle.progress
                if blockers:
                    lo, hi = shuttle.progress, horizon
                    for _ in range(self.limits.search_iterations):
                        mid = (lo+hi)/2
                        if self._blockers(shuttle, mid, current, budget):
                            hi = mid
                        else:
                            lo = mid
                    clear = lo-shuttle.progress
                terminal = not blockers and horizon == self.path.length_m
                available = max(0., clear-(0. if terminal else self.limits.stopping_gap_m))
                safe_speed = max(0., math.sqrt((decel*dt)**2+2*decel*available)-decel*dt)
                shuttle.curr_vel = min(speed, safe_speed)
                proposed[shuttle.ID] = min(proposed[shuttle.ID], shuttle.progress+shuttle.curr_vel*dt)
                if available <= 1e-5:
                    if terminal:
                        proposed[shuttle.ID] = self.path.length_m
                    else:
                        proposed[shuttle.ID] = shuttle.progress
                        blocked.add(shuttle.ID)
                if blockers:
                    self.spatial_diagnostics[self.bodies[shuttle.ID].entity_id] = {"result": "HELD" if shuttle.ID in blocked else "BRAKING",
                                                                                 "blockers": blockers[:16]}

            # Continuous sweeps guard against fast crossing/moving peers. A
            # withdrawn movement becomes a stationary obstacle; recheck all.
            for _ in range(len(self.shuttles)+1):
                sweeps = {s.ID: self._sweep(s, proposed[s.ID], budget) for s in self.shuttles}
                dynamic = SpatialIndex([(str(key), envelope(boxes)) for key, boxes in sweeps.items()])
                changed = False
                for shuttle in self.shuttles:
                    for box in sweeps[shuttle.ID]:
                        hits = self.static_index.query(box, budget)
                        if hits:
                            if proposed[shuttle.ID] != shuttle.progress:
                                proposed[shuttle.ID] = shuttle.progress
                                changed = True
                            blocked.add(shuttle.ID)
                            self.spatial_diagnostics[self.bodies[shuttle.ID].entity_id] = {"result": "HELD", "blockers": hits[:16]}
                    for key in dynamic.query(envelope(sweeps[shuttle.ID]), budget):
                        other = self.shuttles[int(key)]
                        if other.ID <= shuttle.ID:
                            continue
                        conflict = False
                        for a in sweeps[shuttle.ID]:
                            for b in sweeps[other.ID]:
                                budget.spend()
                                conflict |= a.overlaps(b)
                        if not conflict:
                            continue
                        moving = [s for s in (shuttle, other) if proposed[s.ID] != s.progress]
                        losers = [max(moving, key=lambda s: s.ID)] if moving else [shuttle, other]
                        for loser in losers:
                            changed |= proposed[loser.ID] != loser.progress
                            proposed[loser.ID] = loser.progress
                            blocked.add(loser.ID)
                            peer = other if loser.ID == shuttle.ID else shuttle
                            self.spatial_diagnostics[self.bodies[loser.ID].entity_id] = {
                                "result": "HELD", "blockers": [self.bodies[peer.ID].entity_id]}
                if not changed:
                    break
            else:
                raise SpatialUnknown("SPATIAL_RESOLUTION_LIMIT")
        except SpatialUnknown as exc:
            proposed = {s.ID: s.progress for s in self.shuttles}
            blocked = {s.ID for s in self.shuttles}
            self.spatial_diagnostics = {b.entity_id: {"result": "UNKNOWN", "reason": str(exc), "blockers": []} for b in self.bodies}
        self.spatial_work = budget.used
        # A spatial hold withdraws travel; it must not replace charging, dwell
        # or standby with an unrelated route-service state.
        blocked = {s.ID for s in self.shuttles if s.ID in blocked
                   and s.state in {ShuttleState.ROUTE_LOOP, ShuttleState.BLOCKED}}
        return proposed, blocked

    def _state_hash(self):
        document = {"actor": super()._state_hash(), "spatial": self.reference.model_dump(),
                    "diagnostics": self.spatial_diagnostics, "work": self.spatial_work}
        return hashlib.sha256(json.dumps(document, sort_keys=True, separators=(",", ":")).encode()).hexdigest()

    def restore_spatial_state(self, state):
        diagnostics, work = state["spatial_diagnostics"], state["spatial_work"]
        if (not isinstance(diagnostics, dict) or set(diagnostics) != {b.entity_id for b in self.bodies}
                or type(work) is not int or not 0 <= work <= self.limits.max_work_per_tick):
            raise ValueError("INVALID_SPATIAL_CHECKPOINT")
        known_ids = {p.entity_id for p in self.proxies.values()}
        for check in diagnostics.values():
            if (not isinstance(check, dict) or set(check) - {"result", "reason", "blockers"}
                    or check.get("result") not in {"CLEAR", "BRAKING", "HELD", "UNKNOWN"}
                    or not isinstance(check.get("blockers"), list) or len(check["blockers"]) > 16
                    or any(not isinstance(k, str) or k not in known_ids for k in check["blockers"])
                    or (check.get("result") == "UNKNOWN" and check.get("reason") not in {
                        "SPATIAL_WORK_LIMIT", "SWEEP_PIECE_LIMIT", "SPATIAL_RESOLUTION_LIMIT", "REVERSE_NOT_AUTHORED"})):
                raise ValueError("INVALID_SPATIAL_CHECKPOINT")
        self.spatial_diagnostics, self.spatial_work = deepcopy(diagnostics), work

    def validate_restored_shuttles(self):
        if [s.ID for s in self.shuttles] != list(range(len(self.bodies))):
            raise ValueError("INVALID_SPATIAL_ROSTER")
        for shuttle in self.shuttles:
            if (shuttle.route_ID != self.route[0]["route_ID"] or shuttle.direction != 1
                    or shuttle.progress > self.path.length_m or shuttle.curr_vel > self.limits.cruise_mps
                    or shuttle.cruise_vel != self.limits.cruise_mps
                    or shuttle.position != self._route_evaluation(shuttle.route_ID, shuttle.progress)):
                raise ValueError("INVALID_SPATIAL_SHUTTLE_CHECKPOINT")

    def generate_snapshot(self):
        data = super().generate_snapshot().model_dump(mode="json")
        data.update(contract_version="2.1", transform_revision=self.reference.transform_revision,
                    spatial=self.reference.model_dump(), spatial_diagnostics=deepcopy(self.spatial_diagnostics),
                    spatial_work={"used": self.spatial_work, "limit": self.limits.max_work_per_tick,
                                  "collision_margin_m": self.limits.collision_margin_m})
        data["scenario"].update(transform_revision=self.reference.transform_revision, spatial=self.reference.model_dump(),
                                rules_revision=self.limits.revision, vehicle_model_revision="m2-box-body-r1")
        for shuttle, vehicle in zip(sorted(self.shuttles, key=lambda s: s.ID), data["vehicles"]):
            body = self.bodies[shuttle.ID]
            offset = body.dimensions_m[2]/2
            position, orientation, segment = self.path.pose(shuttle.progress, offset)
            vehicle.update(vehicle_id=body.entity_id, position=dict(zip(("x_m", "y_m", "z_m"), position)))
            vehicle["spatial"] = {
                "position_m": list(position), "orientation_xyzw": list(orientation), "dimensions_m": body.dimensions_m,
                "support_offset_m": offset, "support_surface_id": segment.surface_id, "lane_id": segment.lane_id,
                "deck_id": segment.deck_id, "route_progress_m": shuttle.progress,
                "movement": "DISCONTINUITY" if self.current_tick == 0 else "CONTINUOUS" if shuttle.curr_vel > 0 else "HELD",
                "collision_proxy_id": body.proxy_id, "collision_proxy_revision": self.proxies[body.proxy_id].revision,
                "render_asset_id": body.render_asset_id, "render_asset_sha256": self.assets[body.render_asset_id].sha256,
                "asset_to_body": body.asset_to_body,
            }
        # The base actor's charger IDs refer to its sequential private IDs.
        for charger in data["chargers"]:
            index = int(charger["charger_id"].rsplit("-", 1)[1])
            charger["occupied_vehicle_ids"] = [self.bodies[index].entity_id]
        for route in data["routes"]:
            route.update(directed_edge_ids=list(self.edge_ids), edge_paths_m=[list(p) for p in self.edge_paths],
                         estimated_duration_s=self.path.length_m/self.limits.cruise_mps)
        return FleetSnapshot.model_validate(data)


def run_trace():
    """Deterministic authored input/output artifact; no wall-clock claims."""
    actors = [SpatialSessionActor() for _ in range(2)]
    samples = []
    sample_ticks = {0, 1, 10, 20, 40, 60, 80, 100, 120, 160, 200, 240, 300, 400, 600}
    for tick in range(601):
        if tick in sample_ticks:
            snapshot = actors[0].generate_snapshot()
            samples.append({"tick": tick, "state_hash": snapshot.state_hash,
                            "vehicles": [{"id": v.vehicle_id, "state": v.service_state, "battery_kwh": v.battery_kwh,
                                          "position_m": v.spatial.position_m, "orientation_xyzw": v.spatial.orientation_xyzw,
                                          "dimensions_m": v.spatial.dimensions_m, "progress_m": v.spatial.route_progress_m,
                                          "support_surface_id": v.spatial.support_surface_id,
                                          "support_offset_m": v.spatial.support_offset_m}
                                         for v in snapshot.vehicles],
                            "diagnostics": deepcopy(actors[0].spatial_diagnostics), "work": actors[0].spatial_work})
        if tick == 600:
            break
        for actor in actors:
            actor.tick()
        if actors[0]._state_hash() != actors[1]._state_hash():
            raise AssertionError("3D replay diverged")
    return {"artifact_version": 1, "task": "BE-M2-08", "source": "AUTHORED_TRAINING_SIMULATION",
            "spatial": actors[0].reference.model_dump(), "scenario_revision": actors[0].scenario_revision,
            "seed": actors[0].seed, "step_s": actors[0].STEP_S, "verified_steps": 600,
            "replay_matches": True, "inputs": [], "limits": asdict(actors[0].limits), "samples": samples}


def main():
    import argparse
    from pathlib import Path
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--trace", type=Path, help="Write the reproducible slope/stop trace after running")
    args = parser.parse_args()
    trace = run_trace()
    if args.trace:
        args.trace.write_bytes((json.dumps(trace, indent=2, sort_keys=True, allow_nan=False)+"\n").encode("utf-8"))
    print(json.dumps({"verified_steps": trace["verified_steps"], "replay_matches": trace["replay_matches"],
                      "scene_revision": trace["spatial"]["scene_revision"],
                      "state_hash": trace["samples"][-1]["state_hash"],
                      "vehicles": trace["samples"][-1]["vehicles"]}, sort_keys=True))


if __name__ == "__main__":
    main()
