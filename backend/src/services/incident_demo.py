"""Headless incident integration example; this is not the fleet driving actor.

Run from the repo root with PYTHONPATH=backend;. and an explicit database path:
python -m src.services.incident_demo --database build/incident-demo.sqlite --action WAIT
"""

import argparse
import json
from pathlib import Path
from uuid import uuid4

from contracts.v2.models import DispatcherCommand, FullSnapshot
from contracts.v2.route_edit import RouteEditRequest
from src.persistence.training_store import TrainingStore
from src.services.incidents import IncidentService, get_incident_service
from src.services.scene_repository import get_scene_repository


def initial_snapshot(service: IncidentService, session_id: str, *, tick: int = 0) -> FullSnapshot:
    """Authored five-shuttle baseline; no energy forecasts or charge reservations."""
    config, road = service.config, service.routes._fixture
    scene = get_scene_repository().metadata()
    graph = service.routes._graph_repository.get().graph
    start = graph.nodes[road["start_node_id"]]
    source = {"kind": "TRAINING_SIMULATION", "authored": True, "branch_tick": 0}
    snapshot = FullSnapshot.model_validate({
        "contract_version": "2.0", "message_type": "FULL_SNAPSHOT", "snapshot_id": str(uuid4()),
        "session_id": session_id, "session_epoch": 1, "scene_revision": scene.scene_revision,
        "transform_revision": scene.transform_revision, "tick": tick,
        "simulation_time_s": tick * config["step_s"], "event_sequence": 0, "source": source,
        "scenario": {"scenario_id": config["scenario_id"], "revision_id": config["fixture_id"],
                     "scene_revision": scene.scene_revision, "transform_revision": scene.transform_revision,
                     "rules_revision": "rules-r1", "vehicle_model_revision": "shuttle-r1",
                     "energy_model_revision": "energy-r1", "rubric_revision": config["rubric_revision"],
                     "seed": 30, "provenance": source},
        "session": {"session_id": session_id, "scenario_revision_id": config["fixture_id"],
                    "status": "RUNNING", "session_epoch": 1, "aggregate_revision": 1,
                    "authority_id": "headless-incident-demo"},
        "routes": [{"route_id": road["route_id"], "revision": 1, "scene_revision": scene.scene_revision,
                    "directed_edge_ids": road["original_directed_edge_ids"],
                    "required_stop_ids": list(road["required_stops"]),
                    "distance_m": 0.0, "estimated_duration_s": 0.0}],
        "vehicles": [{"vehicle_id": f"shuttle-{i + 1:02d}", "aggregate_revision": 1,
                      "service_state": "IN_SERVICE", "route_id": road["route_id"], "route_revision": 1,
                      "position": {"x_m": float(start["x"]), "y_m": float(start["y"]) + i * 20.0, "z_m": 0.0},
                      "heading_deg": 0.0, "speed_mps": 0.0 if i == 0 else 5.0,
                      "battery_kwh": 28.0, "battery_capacity_kwh": 40.0, "provenance": source}
                     for i in range(5)],
        "incidents": [], "candidates": [], "disruptions": [], "chargers": [],
    })
    metrics = service.routes.preview(snapshot, RouteEditRequest(
        route_id=road["route_id"], expected_route_revision=1, draft_revision=1,
        directed_edge_ids=road["original_directed_edge_ids"],
    ), retain=False).original
    snapshot.routes[0].distance_m = metrics.distance_m
    snapshot.routes[0].estimated_duration_s = metrics.duration_s
    return snapshot


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", type=Path, required=True)
    parser.add_argument("--action", choices=["WAIT", "ESCALATE", "ROAD_DETOUR"], default="WAIT")
    arguments = parser.parse_args()
    service = get_incident_service()
    session_id = "incident-demo-" + str(uuid4())
    with TrainingStore(arguments.database) as store:
        store.create_session(initial_snapshot(service, session_id))

        def publish_step():
            proposed = store.snapshot(session_id)
            proposed.tick += 2
            proposed.simulation_time_s = proposed.tick * 0.05
            # Peer motion is synthetic evidence for the incident hook, not road driving.
            for peer in proposed.vehicles[1:]:
                peer.position.x_m += peer.speed_mps * 0.1
            return service.publish(store, proposed)

        for _ in range(130):
            snapshot = publish_step()
        incident = snapshot.incidents[0]
        option = next(c for c in snapshot.candidates if c.action == arguments.action)
        result = service.decide(store, "headless-demo", DispatcherCommand(
            contract_version="2.0", command_id=str(uuid4()), idempotency_key=str(uuid4()),
            session_id=session_id, command_type="APPROVE_INCIDENT_OPTION", expected_epoch=snapshot.session_epoch,
            expected_revision=incident.aggregate_revision, target_type="INCIDENT", target_id=incident.incident_id,
            payload={"candidate_id": option.candidate_id}, reason="Use an acceptable authored incident response",
        ))
        if arguments.action != "ROAD_DETOUR":
            while store.snapshot(session_id).incidents[0].state in {"WAITING", "ESCALATED"}:
                publish_step()
        debrief = service.debrief(store, session_id, incident.incident_id)
        print(json.dumps({"database": str(store.path), "session_id": session_id,
                          "command_status": result.status, "incident_state": debrief["state"],
                          "history_complete": service.history(store, session_id, incident.incident_id)["complete"],
                          "decision": debrief["decisions"][-1], "outcome": debrief["outcome"]}, indent=2))


if __name__ == "__main__":
    main()
