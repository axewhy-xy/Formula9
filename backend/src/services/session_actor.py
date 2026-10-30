"""Headless, fixed-step fleet controller for the authored M2 planar fixture.

From the repository root, set PYTHONPATH=backend;. and run
python -m src.services.session_actor. Network ownership and durable publication
belong to the session host; generating a snapshot never advances the actor.
"""

from __future__ import annotations

from collections import deque
from dataclasses import asdict
import hashlib
import json
import math
import random

from pydantic import TypeAdapter

from contracts.v2.models import Id
from ..energy.model import ENERGY_R1, charge_battery_kwh, use_energy
from ..schemas.shuttle_data import (
    Command, CommandOutcome, CommandType, FleetSnapshot, Shuttle, ShuttleState, Vector2D,
)
from .asset_loader import load_routes_from_json, validate_routes


class SessionActor:
    """One sequential authority; all motion is checked before it is applied.

    The bounded fixture uses point centres with a conservative 10 m separation,
    15 m same-route headway and 20 m release hysteresis. This is a 2D training
    controller, not the later BE-M2-08 swept 3D body controller.
    """

    STEP_S = 0.05
    SEPARATION_M = 10.0
    HEADWAY_M = 15.0
    RELEASE_M = 20.0
    DWELL_S = 2.0
    BATTERY_CAPACITY_KWH = 40.0
    CHARGER_POWER_KW = 50.0
    MAX_PENDING_COMMANDS = 256
    MAX_RETAINED_OUTCOMES = 1024

    def __init__(self, seed: int, route: list[dict], fixed_DT: float = 0.05,
                 shuttle_cnt: int = 5, *, session_id: str = "fleet-session"):
        if fixed_DT != self.STEP_S:
            raise ValueError("The fleet contract requires a fixed 50 ms step")
        if type(shuttle_cnt) is not int or not 0 <= shuttle_cnt <= 5:
            raise ValueError("The curated fixture supports at most five shuttles")
        if type(seed) is not int:
            raise ValueError("The fixture seed must be an integer")
        self.session_id = TypeAdapter(Id).validate_python(session_id)
        self.seed = seed
        self.fixed_DT = fixed_DT
        self.shuttle_cnt = shuttle_cnt
        self.route = self._validated_routes(route)
        self._validate_assignments()
        self._routes = {r["route_ID"]: r for r in self.route}
        geometry = [{**r, "waypoints": [p.model_dump() for p in r["waypoints"]]} for r in self.route]
        manifest = json.dumps(geometry, sort_keys=True, separators=(",", ":"), allow_nan=False)
        self.scene_revision = f"fleet-scene-{hashlib.sha256(manifest.encode()).hexdigest()[:24]}"
        scenario = json.dumps({"scene": self.scene_revision, "seed": seed, "rules": "fleet-rules-r2",
                               "energy": asdict(ENERGY_R1), "capacity_kwh": self.BATTERY_CAPACITY_KWH,
                               "charge_kw": self.CHARGER_POWER_KW, "step_s": self.fixed_DT,
                               "shuttle_count": shuttle_cnt, "separation_m": self.SEPARATION_M,
                               "headway_m": self.HEADWAY_M, "release_m": self.RELEASE_M,
                               "dwell_s": self.DWELL_S}, sort_keys=True, separators=(",", ":"))
        self.scenario_revision = f"fleet-scenario-{hashlib.sha256(scenario.encode()).hexdigest()[:24]}"
        self.command_tick = 0
        self.session_epoch = 1
        self.command_queue: list[Command] = []
        self.command_outcome: deque[CommandOutcome] = deque(maxlen=self.MAX_RETAINED_OUTCOMES)
        self.command_revision = 0
        self._reset_simulation_state()

    def _validated_routes(self, route: list[dict]) -> list[dict]:
        return validate_routes(route)

    def _validate_assignments(self) -> None:
        # Open paths reverse at endpoints; sharing requires oncoming-traffic scheduling.
        for index in range(len(self.route), self.shuttle_cnt):
            assigned = self.route[index % len(self.route)]
            if assigned["waypoints"][0] != assigned["waypoints"][-1]:
                raise ValueError(f"Open route {assigned['route_ID']!r} cannot be assigned to multiple shuttles")

    def _reset_simulation_state(self) -> None:
        self.current_tick = 0
        self.timestamp_second = 0.0
        self.is_paused = False
        self.rng = random.Random(self.seed)
        self.shuttles = []
        for index in range(self.shuttle_cnt):
            route = self.route[index % len(self.route)]
            progress = min(float(index * 20), route["total_length"])
            at_endpoint = progress == route["total_length"]
            self.shuttles.append(Shuttle(
                ID=index, route_ID=route["route_ID"],
                state=ShuttleState.STOP_DWELL if at_endpoint else ShuttleState.ROUTE_LOOP,
                progress=progress, curr_vel=0.0 if at_endpoint else 20.0, battery=100.0 - index * 10,
                dwell_timer=self.DWELL_S if at_endpoint else 0.0,
                position=self._route_evaluation(route["route_ID"], progress),
            ))

    def _route_evaluation(self, route_ID: int | str, progress: float) -> Vector2D:
        points = self._routes[route_ID]["waypoints"]
        remaining = max(0.0, progress)
        for a, b in zip(points, points[1:]):
            length = math.hypot(b.x - a.x, b.y - a.y)
            if remaining <= length:
                t = remaining / length
                return Vector2D(x=a.x + t * (b.x - a.x), y=a.y + t * (b.y - a.y))
            remaining -= length
        return points[-1].model_copy(deep=True)

    def _is_closed(self, shuttle: Shuttle) -> bool:
        points = self._routes[shuttle.route_ID]["waypoints"]
        return points[0] == points[-1]

    def enqueue_command(self, cmd: Command) -> None:
        if len(self.command_queue) >= self.MAX_PENDING_COMMANDS:
            raise ValueError("Fleet control queue is full")
        self.command_queue.append(cmd.model_copy(deep=True))
        self.command_queue.sort(key=lambda item: item.triggered_tick)

    def _handle_commands(self) -> bool:
        # Capture the entire due batch before reset changes the simulation clock.
        due = [cmd for cmd in self.command_queue if cmd.triggered_tick <= self.command_tick]
        self.command_queue = self.command_queue[len(due):]
        reset = False
        for cmd in due:
            if cmd.type == CommandType.RESET:
                self._reset_simulation_state()
                self.session_epoch += 1
                reset = True
            else:
                self.is_paused = cmd.type == CommandType.PAUSE
            self.command_outcome.append(CommandOutcome(
                processing_tick=self.command_tick, command_type=cmd.type,
                message=f"{cmd.type.value} processed at command tick {self.command_tick}.",
            ))
            self.command_revision += 1
        return reset

    def _resume_route(self, shuttle: Shuttle) -> None:
        shuttle.state = ShuttleState.ROUTE_LOOP
        shuttle.curr_vel = shuttle.cruise_vel

    def _prepare_motion(self, shuttle: Shuttle) -> float:
        """Advance stationary state machines, then propose an absolute route progress."""
        if shuttle.state == ShuttleState.STOP_DWELL:
            shuttle.curr_vel = 0.0
            shuttle.dwell_timer = max(0.0, shuttle.dwell_timer - self.fixed_DT)
            if shuttle.dwell_timer <= 1e-9:
                shuttle.dwell_timer = 0.0
                length = self._routes[shuttle.route_ID]["total_length"]
                if self._is_closed(shuttle):
                    # Both endpoints are the same physical location.
                    shuttle.progress = 0.0
                elif shuttle.progress >= length:
                    shuttle.direction = -1
                elif shuttle.progress <= 0:
                    shuttle.direction = 1
                self._resume_route(shuttle)
            return shuttle.progress
        if shuttle.state == ShuttleState.DEPOT_STANDBY:
            shuttle.curr_vel = 0.0
            if shuttle.battery < 30.0:
                shuttle.state = ShuttleState.CHARGING
            return shuttle.progress
        if shuttle.state == ShuttleState.CHARGING:
            shuttle.curr_vel = 0.0
            level = charge_battery_kwh(
                ENERGY_R1, shuttle.battery * self.BATTERY_CAPACITY_KWH / 100,
                self.BATTERY_CAPACITY_KWH, self.CHARGER_POWER_KW, self.fixed_DT,
            )
            level, _ = use_energy(ENERGY_R1, level, 0.0, self.fixed_DT)
            shuttle.battery = 100 * level / self.BATTERY_CAPACITY_KWH
            if shuttle.battery >= 60.0:
                self._resume_route(shuttle)
            return shuttle.progress
        if shuttle.battery < 30.0:
            shuttle.state = ShuttleState.DEPOT_STANDBY
            shuttle.curr_vel = 0.0
            return shuttle.progress
        if shuttle.state == ShuttleState.BLOCKED:
            speed = shuttle.cruise_vel
        else:
            speed = shuttle.curr_vel
            if speed > 0:
                shuttle.cruise_vel = speed
        length = self._routes[shuttle.route_ID]["total_length"]
        return max(0.0, min(length, shuttle.progress + shuttle.direction * speed * self.fixed_DT))

    def _trajectory(self, shuttle: Shuttle, end: float) -> list[tuple[float, Vector2D]]:
        """Piecewise linear positions over normalized step time, including every bend."""
        start = shuttle.progress
        if abs(end - start) <= 1e-12:
            return [(0.0, self._route_evaluation(shuttle.route_ID, start)),
                    (1.0, self._route_evaluation(shuttle.route_ID, start))]
        distances = []
        progress = 0.0
        points = self._routes[shuttle.route_ID]["waypoints"]
        for a, b in zip(points, points[1:]):
            progress += math.hypot(b.x - a.x, b.y - a.y)
            if min(start, end) < progress < max(start, end):
                distances.append(progress)
        if end < start:
            distances.reverse()
        return [((p - start) / (end - start), self._route_evaluation(shuttle.route_ID, p))
                for p in [start, *distances, end]]

    @staticmethod
    def _sample(path: list[tuple[float, Vector2D]], t: float) -> Vector2D:
        for (ta, a), (tb, b) in zip(path, path[1:]):
            if t <= tb:
                fraction = (t - ta) / (tb - ta)
                return Vector2D(x=a.x + fraction * (b.x - a.x), y=a.y + fraction * (b.y - a.y))
        return path[-1][1]

    def _conflicts(self, first: Shuttle, first_end: float, second: Shuttle, second_end: float) -> bool:
        a = self._trajectory(first, first_end)
        b = self._trajectory(second, second_end)
        times = sorted({t for t, _ in a} | {t for t, _ in b})
        for t0, t1 in zip(times, times[1:]):
            a0, a1 = self._sample(a, t0), self._sample(a, t1)
            b0, b1 = self._sample(b, t0), self._sample(b, t1)
            x, y = a0.x - b0.x, a0.y - b0.y
            dx, dy = (a1.x - b1.x) - x, (a1.y - b1.y) - y
            length_sq = dx * dx + dy * dy
            t = max(0.0, min(1.0, -(x * dx + y * dy) / length_sq)) if length_sq else 0.0
            if math.hypot(x + t * dx, y + t * dy) < self.SEPARATION_M - 1e-9:
                return True
        return False

    def _resolve_motion(self, proposed: dict[int, float]) -> tuple[dict[int, float], set[int]]:
        """Gather every constraint; stopping one peer can only add restrictions."""
        blocked = set()
        for shuttle in self.shuttles:
            if shuttle.state not in {ShuttleState.ROUTE_LOOP, ShuttleState.BLOCKED}:
                continue
            threshold = self.RELEASE_M if shuttle.state == ShuttleState.BLOCKED else self.HEADWAY_M
            for other in self.shuttles:
                if other.ID == shuttle.ID or other.route_ID != shuttle.route_ID:
                    continue
                gap = shuttle.direction * (other.progress - shuttle.progress)
                if self._is_closed(shuttle):
                    gap %= self._routes[shuttle.route_ID]["total_length"]
                if 0 <= gap < threshold:
                    proposed[shuttle.ID] = shuttle.progress
                    blocked.add(shuttle.ID)
                elif gap >= threshold:
                    travel = abs(proposed[shuttle.ID] - shuttle.progress)
                    if travel > gap - self.HEADWAY_M:
                        proposed[shuttle.ID] = shuttle.progress + shuttle.direction * max(0.0, gap - self.HEADWAY_M)
                        blocked.add(shuttle.ID)

        # At most five movements can be withdrawn. Recheck after each withdrawal
        # because a previously moving vehicle has become a stationary obstacle.
        for _ in range(len(self.shuttles) + 1):
            changed = False
            for i, first in enumerate(self.shuttles):
                for second in self.shuttles[i + 1:]:
                    if not self._conflicts(first, proposed[first.ID], second, proposed[second.ID]):
                        continue
                    moving = [s for s in (first, second) if proposed[s.ID] != s.progress]
                    # Prefer the lower ID only when its path is safe with its peer stopped.
                    if moving:
                        loser = max(moving, key=lambda s: s.ID)
                        proposed[loser.ID] = loser.progress
                        blocked.add(loser.ID)
                        changed = True
                    else:
                        for shuttle in (first, second):
                            if shuttle.state in {ShuttleState.ROUTE_LOOP, ShuttleState.BLOCKED}:
                                blocked.add(shuttle.ID)
            if not changed:
                break
        return proposed, blocked

    def tick(self) -> None:
        self.command_tick += 1
        reset = self._handle_commands()
        if reset or self.is_paused:
            return
        self.current_tick += 1
        self.timestamp_second = self.current_tick * self.fixed_DT
        self.shuttles.sort(key=lambda shuttle: shuttle.ID)
        charging = {s.ID for s in self.shuttles if s.state == ShuttleState.CHARGING}
        proposed = {s.ID: self._prepare_motion(s) for s in self.shuttles}
        proposed, blocked = self._resolve_motion(proposed)
        for shuttle in self.shuttles:
            distance = abs(proposed[shuttle.ID] - shuttle.progress)
            shuttle.progress = proposed[shuttle.ID]
            if shuttle.ID in blocked:
                shuttle.state = ShuttleState.BLOCKED
                shuttle.curr_vel = 0.0
            elif shuttle.state == ShuttleState.BLOCKED:
                self._resume_route(shuttle)
            length = self._routes[shuttle.route_ID]["total_length"]
            if distance > 0 and shuttle.progress in (0.0, length):
                shuttle.state = ShuttleState.STOP_DWELL
                shuttle.dwell_timer = self.DWELL_S
                shuttle.curr_vel = 0.0
            if shuttle.ID not in charging:
                level, _ = use_energy(ENERGY_R1, shuttle.battery * self.BATTERY_CAPACITY_KWH / 100,
                                      distance, self.fixed_DT)
                shuttle.battery = 100 * level / self.BATTERY_CAPACITY_KWH
                if shuttle.battery < 30 and shuttle.state in {ShuttleState.ROUTE_LOOP, ShuttleState.BLOCKED}:
                    shuttle.state = ShuttleState.DEPOT_STANDBY
                    shuttle.curr_vel = 0.0
            shuttle.position = self._route_evaluation(shuttle.route_ID, shuttle.progress)

    def should_emit_snapshot(self) -> bool:
        # Command clock maintains 10 Hz publication even while simulation time is frozen.
        return self.command_tick % 2 == 0

    def _state_hash(self) -> str:
        state = {
            "seed": self.seed, "rng": self.rng.getstate(), "step_s": self.fixed_DT,
            "tick": self.current_tick, "command_tick": self.command_tick,
            "epoch": self.session_epoch, "paused": self.is_paused,
            "command_revision": self.command_revision,
            "session_id": self.session_id, "shuttle_count": self.shuttle_cnt,
            "scene_revision": self.scene_revision, "scenario_revision": self.scenario_revision,
            "model": {"energy": asdict(ENERGY_R1), "capacity_kwh": self.BATTERY_CAPACITY_KWH,
                      "charge_kw": self.CHARGER_POWER_KW, "separation_m": self.SEPARATION_M,
                      "headway_m": self.HEADWAY_M, "release_m": self.RELEASE_M, "dwell_s": self.DWELL_S},
            "routes": [{**r, "waypoints": [p.model_dump() for p in r["waypoints"]]} for r in self.route],
            "shuttles": [s.model_dump(mode="json") for s in sorted(self.shuttles, key=lambda s: s.ID)],
            "commands": [c.model_dump(mode="json") for c in self.command_queue],
            "outcomes": [c.model_dump(mode="json") for c in self.command_outcome],
        }
        return hashlib.sha256(json.dumps(state, sort_keys=True, separators=(",", ":"), allow_nan=False).encode()).hexdigest()

    def generate_snapshot(self) -> FleetSnapshot:
        """Build a detached, directly parseable v2 snapshot without advancing state."""
        source = {"kind": "TRAINING_SIMULATION", "authored": True}
        states = {
            ShuttleState.ROUTE_LOOP: "IN_SERVICE", ShuttleState.STOP_DWELL: "DWELLING",
            ShuttleState.DEPOT_STANDBY: "STANDBY", ShuttleState.BLOCKED: "BLOCKED",
            ShuttleState.CHARGING: "CHARGING",
        }
        vehicles = []
        chargers = []
        for shuttle in sorted(self.shuttles, key=lambda s: s.ID):
            vehicle_id = f"shuttle-{shuttle.ID + 1:02}"
            ahead = self._route_evaluation(shuttle.route_ID, shuttle.progress + 0.01 * shuttle.direction)
            behind = self._route_evaluation(shuttle.route_ID, shuttle.progress - 0.01 * shuttle.direction)
            heading = math.degrees(math.atan2(ahead.y - behind.y, ahead.x - behind.x))
            vehicles.append({
                "vehicle_id": vehicle_id, "aggregate_revision": self.command_tick,
                "service_state": states[shuttle.state], "route_id": str(shuttle.route_ID), "route_revision": 1,
                "position": {"x_m": shuttle.position.x, "y_m": shuttle.position.y, "z_m": 0.0},
                "heading_deg": heading, "speed_mps": shuttle.curr_vel,
                "battery_kwh": shuttle.battery * self.BATTERY_CAPACITY_KWH / 100,
                "battery_capacity_kwh": self.BATTERY_CAPACITY_KWH, "provenance": dict(source),
            })
            if shuttle.state == ShuttleState.CHARGING:
                chargers.append({"charger_id": f"fixture-charger-{shuttle.ID}",
                                 "aggregate_revision": self.command_tick, "port_count": 1,
                                 "power_kw": self.CHARGER_POWER_KW, "occupied_vehicle_ids": [vehicle_id]})
        routes = [{"route_id": str(r["route_ID"]), "revision": 1, "scene_revision": self.scene_revision,
                   "directed_edge_ids": [f"edge-{hashlib.sha256(str(r['route_ID']).encode()).hexdigest()[:16]}-{i}"
                                         for i in range(len(r["waypoints"]) - 1)],
                   "required_stop_ids": [], "distance_m": r["total_length"],
                   "estimated_duration_s": r["total_length"] / 20.0,
                   # Edge i is the straight leg between waypoints i and i + 1
                   "edge_paths_m": [[{"x_m": a.x, "y_m": a.y, "z_m": 0.0}, {"x_m": b.x, "y_m": b.y, "z_m": 0.0}]
                                    for a, b in zip(r["waypoints"], r["waypoints"][1:])]} for r in self.route]
        digest = self._state_hash()
        return FleetSnapshot(
            contract_version="2.0", message_type="FULL_SNAPSHOT", session_id=self.session_id,
            session_epoch=self.session_epoch, scene_revision=self.scene_revision, transform_revision="fleet-transform-r1",
            tick=self.current_tick, simulation_time_s=self.timestamp_second, event_sequence=0,
            source=source, snapshot_id=f"snapshot-{digest[:32]}", state_hash=digest,
            scenario={"scenario_id": "fleet-fixture", "revision_id": self.scenario_revision,
                      "scene_revision": self.scene_revision, "transform_revision": "fleet-transform-r1",
                      "rules_revision": "fleet-rules-r2", "vehicle_model_revision": "fleet-vehicle-r1",
                      "energy_model_revision": ENERGY_R1.revision, "rubric_revision": "fleet-rubric-r1",
                      "seed": self.seed, "provenance": source},
            session={"session_id": self.session_id, "scenario_revision_id": self.scenario_revision,
                     "status": "PAUSED" if self.is_paused else "RUNNING", "session_epoch": self.session_epoch,
                     "aggregate_revision": self.command_revision, "authority_id": "backend-fleet-actor"},
            vehicles=vehicles, routes=routes, disruptions=[], incidents=[], candidates=[], chargers=chargers,
        )


def main() -> None:
    """Exercise uninterrupted motion and commands for 30 s; never substitute assets."""
    routes = load_routes_from_json()
    first, second = (SessionActor(seed=1000, route=routes) for _ in range(2))
    for actor in (first, second):
        for kind, boundary in [(CommandType.PAUSE, 100), (CommandType.RESUME, 120), (CommandType.RESET, 400)]:
            actor.enqueue_command(Command(type=kind, triggered_tick=boundary))
    emitted = 0
    for _ in range(600):
        first.tick()
        second.tick()
        a, b = first.generate_snapshot(), second.generate_snapshot()
        if a.state_hash != b.state_hash:
            raise AssertionError(f"Determinism failure at command tick {first.command_tick}")
        if first.should_emit_snapshot():
            emitted += 1
    print(json.dumps({"verified_command_steps": 600, "shuttles": len(first.shuttles),
                      "presentation_snapshots": emitted, "seed": first.seed,
                      "scene_revision": first.scene_revision, "scenario_revision": first.scenario_revision,
                      "final_epoch": first.session_epoch, "final_tick": first.current_tick,
                      "state_hash": first._state_hash()}, sort_keys=True))


if __name__ == "__main__":
    main()
