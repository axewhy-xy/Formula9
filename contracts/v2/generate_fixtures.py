"""Regenerate the checked-in v2 JSON Schema and shared golden fixtures."""

import copy
import json
from pathlib import Path

from pydantic import TypeAdapter

from contracts.v2.models import DispatcherCommand, Message, parse_message
from contracts.v2.route_edit import RouteApprovalRequest, RouteEditPreview, RouteEditRequest, RouteEditorContext, RouteOutcome
from contracts.v2.sessions import CreateSessionRequest, SessionListResponse

ROOT = Path(__file__).parent
FIXTURES = ROOT / "fixtures"
FIXTURES.mkdir(exist_ok=True)


def save(name: str, data: dict) -> None:
    (FIXTURES / name).write_text(json.dumps(data, indent=2, sort_keys=True) + "\n", encoding="utf-8")


creation_schema = CreateSessionRequest.model_json_schema()
creation_schema["$id"] = "https://formula9.local/contracts/v2/create_session.schema.json"
(ROOT / "create_session.schema.json").write_text(json.dumps(creation_schema, indent=2, sort_keys=True) + "\n", encoding="utf-8")
save("create_session.json", CreateSessionRequest(scenario_revision_id="scenario-m2-r1").model_dump(mode="json"))

session_list_schema = SessionListResponse.model_json_schema()
session_list_schema["$id"] = "https://formula9.local/contracts/v2/session_list.schema.json"
(ROOT / "session_list.schema.json").write_text(json.dumps(session_list_schema, indent=2, sort_keys=True) + "\n", encoding="utf-8")
save("session_list.json", SessionListResponse(sessions=[
    {"session_id": "session-001", "scenario_revision_id": "scenario-r1", "status": "RUNNING", "occupies_capacity": True},
    {"session_id": "session-old", "scenario_revision_id": "scenario-r1", "status": "ARCHIVED", "occupies_capacity": False},
], live_count=1, total=2, capacity=16).model_dump(mode="json"))


schema = TypeAdapter(Message).json_schema()
schema["$id"] = "https://formula9.local/contracts/v2/message.schema.json"
schema["title"] = "Formula 9 training message v2"
(ROOT / "message.schema.json").write_text(json.dumps(schema, indent=2, sort_keys=True) + "\n", encoding="utf-8")
command_schema = DispatcherCommand.model_json_schema()
command_schema["$id"] = "https://formula9.local/contracts/v2/command.schema.json"
(ROOT / "command.schema.json").write_text(json.dumps(command_schema, indent=2, sort_keys=True) + "\n", encoding="utf-8")
for name, model in (("route_edit_request", RouteEditRequest),
                    ("route_editor_context", RouteEditorContext),
                    ("route_edit_preview", RouteEditPreview),
                    ("route_approval", RouteApprovalRequest),
                    ("route_outcome", RouteOutcome)):
    route_schema = model.model_json_schema()
    route_schema["$id"] = f"https://formula9.local/contracts/v2/{name}.schema.json"
    (ROOT / f"{name}.schema.json").write_text(
        json.dumps(route_schema, indent=2, sort_keys=True) + "\n", encoding="utf-8")

simulation = {"kind": "TRAINING_SIMULATION", "recording_id": None, "sequence_id": None,
              "source_frame": None, "source_timestamp": None, "branch_tick": None, "authored": True}
envelope = {"contract_version": "2.0", "session_id": "session-001", "session_epoch": 1,
            "scene_revision": "scene-r1", "transform_revision": "transform-r1", "tick": 200,
            "simulation_time_s": 10.0, "event_sequence": 7, "source": simulation}
snapshot = {**envelope, "message_type": "FULL_SNAPSHOT", "snapshot_id": "snapshot-200",
            "scenario": {"scenario_id": "scenario-m2", "revision_id": "scenario-m2-r1",
                         "scene_revision": "scene-r1", "transform_revision": "transform-r1",
                         "rules_revision": "rules-r1", "vehicle_model_revision": "vehicle-r1",
                         "energy_model_revision": "energy-r1", "rubric_revision": "rubric-r1",
                         "seed": 142, "provenance": simulation},
            "session": {"session_id": "session-001", "scenario_revision_id": "scenario-m2-r1",
                        "status": "RUNNING", "session_epoch": 1, "aggregate_revision": 3,
                        "authority_id": "backend-001"},
            "routes": [{"route_id": "route-a", "revision": 2, "scene_revision": "scene-r1",
                        "directed_edge_ids": ["edge-1", "edge-2"], "required_stop_ids": ["stop-1"],
                        "distance_m": 1200.0, "estimated_duration_s": 180.0}],
            "vehicles": [], "disruptions": [], "incidents": [], "candidates": [], "chargers": []}
for index in range(1, 6):
    snapshot["vehicles"].append({"vehicle_id": f"shuttle-{index:02}", "aggregate_revision": 1,
                                 "service_state": "IN_SERVICE", "route_id": "route-a", "route_revision": 2,
                                 "position": {"x_m": float(index * 10), "y_m": 0.0, "z_m": 0.0},
                                 "heading_deg": 0.0, "speed_mps": 5.0, "battery_kwh": 28.0,
                                 "battery_capacity_kwh": 40.0, "provenance": simulation})
snapshot["disruptions"].append({"disruption_id": "closure-1", "aggregate_revision": 1,
                                "affected_edge_ids": ["edge-2"], "active_from_tick": 100,
                                "active_until_tick": None, "reason": "Scripted road closure",
                                "provenance": simulation})
snapshot["incidents"].append({"incident_id": "incident-1", "aggregate_revision": 2,
                              "vehicle_id": "shuttle-01", "state": "OPTIONS_READY", "detected_tick": 180,
                              "candidate_ids": ["candidate-wait-1"], "provenance": simulation})
snapshot["candidates"].append({"candidate_id": "candidate-wait-1", "incident_id": "incident-1",
                               "revision": 1, "action": "WAIT", "validation": "PASS",
                               "expires_at_tick": 300, "dependency_revisions": {"incident-1": 2},
                               "assumptions": ["Obstruction clears within 5 simulated seconds"]})
snapshot["chargers"].append({"charger_id": "charger-1", "aggregate_revision": 1,
                             "port_count": 1, "power_kw": 50.0, "occupied_vehicle_ids": []})
snapshot["chargers"].append({"charger_id": "charger-2", "aggregate_revision": 1,
                             "port_count": 1, "power_kw": 50.0, "occupied_vehicle_ids": []})
snapshot["depots"] = [{"depot_id": "depot-1",
                       "position": {"x_m": 0.0, "y_m": 0.0, "z_m": 0.0},
                       "charger_ids": ["charger-1", "charger-2"]}]
snapshot["energy_forecasts"] = []
for index in range(1, 6):
    snapshot["energy_forecasts"].append({
        "vehicle_id": f"shuttle-{index:02}",
        "energy_model_revision": "energy-r1",
        "reserve_kwh": 6.0,
        "reserve_margin_kwh": 22.0,
        "loops_remaining": 58,
        "next_depot_id": "depot-1",
        "next_depot_eta_s": (1200.0 - index * 10) / 5.0,
        "reaches_depot_above_reserve": True,
        "is_estimate": True,
    })

low_reserve = copy.deepcopy(snapshot)
low_reserve["snapshot_id"] = "snapshot-200-low-reserve"
low_reserve["vehicles"][4]["battery_kwh"] = 7.0               # shuttle-05
low_reserve["energy_forecasts"][4].update(reserve_margin_kwh=1.0, loops_remaining=2)
low_reserve["charge_options"] = [
    {"option_id": "charge-option-shuttle-05-continue", "vehicle_id": "shuttle-05", "revision": 1,
     "action": "CONTINUE", "charger_id": None, "validation": "PASS", "expires_at_tick": 300,
     "offered_at_tick": 200,
     "dependency_revisions": {"shuttle-05": 1}, "out_of_service_s": 0.0,
     "battery_after_kwh": 7.0, "loops_after": 2, "min_in_service_vehicles": 5,
     "assumptions": ["Reserve breached after 2 more loops"], "is_estimate": True},
    {"option_id": "charge-option-shuttle-05-charger-1", "vehicle_id": "shuttle-05", "revision": 1,
     "action": "CHARGE_NOW", "charger_id": "charger-1", "validation": "PASS", "expires_at_tick": 300,
     "offered_at_tick": 200,
     "dependency_revisions": {"shuttle-05": 1, "charger-1": 1}, "out_of_service_s": 1200.0,
     "battery_after_kwh": 22.0, "loops_after": 42, "min_in_service_vehicles": 4,
     "assumptions": ["charger-1 stays free for 20 minutes"], "is_estimate": True},
]
low_reserve["charge_option_revisions"] = {o["option_id"]: o["revision"] for o in low_reserve["charge_options"]}
parse_message(low_reserve)
save("low_reserve_snapshot.json", low_reserve)

event = {"event_id": "event-7", "event_sequence": 7, "tick": 200,
         "entity_type": "INCIDENT", "entity_id": "incident-1", "entity_revision": 2,
         "event_type": "OPTIONS_READY", "source": simulation,
         "data": {"candidate_ids": ["candidate-wait-1"]}}
replay = {**envelope, "message_type": "EVENT_REPLAY", "after_sequence": 6,
          "events": [event], "next_sequence": 7, "has_more": False}
status = {**envelope, "message_type": "COMMAND_STATUS", "command_id": "command-1",
          "idempotency_key": "client-key-1", "status": "APPLIED", "target_type": "INCIDENT",
          "target_id": "incident-1", "target_revision": 2, "applied_tick": 200,
          "result_event_id": "event-7", "failure_code": None}
error = {**envelope, "message_type": "ERROR", "correlation_id": "request-1",
         "error": {"code": "STALE_REVISION", "message": "Incident revision changed",
                   "field": "expected_revision", "retryable": True, "current_revision": 2}}
for name, value in (("snapshot.json", snapshot), ("event_replay.json", replay),
                    ("command_status.json", status), ("error.json", error)):
    parse_message(value)
    save(name, value)

command = {"contract_version": "2.0", "command_id": "command-1", "idempotency_key": "client-key-1",
           "session_id": "session-001", "command_type": "APPROVE_INCIDENT_OPTION",
           "expected_epoch": 1, "expected_revision": 2, "target_type": "INCIDENT",
           "target_id": "incident-1", "payload": {"candidate_id": "candidate-wait-1"},
           "reason": "Wait for the scripted obstruction to clear"}
DispatcherCommand.model_validate(command)
save("command.json", command)

negative = {}
for name, mutation in (
    ("invalid_enum", lambda v: v["vehicles"][0].update(service_state="TELEPORTING")),
    ("missing_reference", lambda v: v["vehicles"][0].update(route_id="route-missing")),
    ("stale_scene_revision", lambda v: v.update(scene_revision="scene-old")),
    ("incompatible_major", lambda v: v.update(contract_version="3.0")),
):
    value = copy.deepcopy(snapshot)
    mutation(value)
    negative[name] = value
for name, value in negative.items():
    try:
        parse_message(value)
    except ValueError:
        save(f"{name}.json", value)
    else:
        raise AssertionError(f"Negative fixture accepted: {name}")

gap = copy.deepcopy(replay)
gap["events"][0]["event_sequence"] = 8
try:
    parse_message(gap)
except ValueError:
    save("invalid_event_gap.json", gap)
else:
    raise AssertionError("Replay gap accepted")

route_draft = {"contract_version": "2.0", "route_id": "route-m2-detour",
               "expected_route_revision": 1, "draft_revision": 3,
               "via_node_ids": [26970886], "directed_edge_ids": None}
save("route_edit_request.json", RouteEditRequest.model_validate(route_draft).model_dump(mode="json"))
route_preview = {
    "contract_version": "2.0", "preview_id": "preview-m2-1", "preview_revision": 3,
    "session_id": "session-001", "session_epoch": 1, "route_id": "route-m2-detour",
    "route_revision": 1, "scene_revision": "scene-r1", "fixture_id": "m2-pretrip-detour-r1",
    "closure_revisions": {"closure-m2": 1},
    "directed_edge_ids": ["1535209095:0:reverse", "440462565:12:forward",
                          "5061138:0:reverse", "51894042:0:forward",
                          "51894042:1:forward", "51894042:2:forward"],
    "node_ids": [26970786, 1329728439, 26970880, 26970886,
                 1370249326, 1370249338, 662221987],
    "polyline": [{"x": float(index * 10), "y": 0.0, "z": 0.0}
                 for index in range(7)],
    "checks": [{"check": name, "status": "PASS", "message": "Curated check passed",
                "feature_id": None, "mandatory": True}
               for name in ("GEOMETRY", "DIRECTION", "CLOSURE", "STOPS",
                            "OPERATING_AREA", "VEHICLE_FIT", "TURNS")],
    "validation": "PASS", "approvable": True,
    "original": {"distance_m": 100.0, "duration_s": 33.0,
                 "stop_coverage": {"stop-terminal": True}, "energy_kwh": 0.0938},
    "edited": {"distance_m": 250.0, "duration_s": 60.0,
               "stop_coverage": {"stop-terminal": True}, "energy_kwh": 0.225},
}
save("route_edit_preview.json", RouteEditPreview.model_validate(route_preview).model_dump(mode="json"))
route_context = {
    "contract_version": "2.0", "session_id": "session-001", "session_epoch": 1,
    "route_id": "route-m2-detour", "route_revision": 1, "scene_revision": "scene-r1",
    "fixture_id": "m2-pretrip-detour-r1", "start_node_id": route_preview["node_ids"][0],
    "destination_node_id": route_preview["node_ids"][-1],
    "nodes": [{"node_id": node, **point} for node, point in zip(route_preview["node_ids"], route_preview["polyline"])],
    "edges": [{"edge_id": edge, "source": a, "target": b, "closed": False}
              for edge, a, b in zip(route_preview["directed_edge_ids"], route_preview["node_ids"], route_preview["node_ids"][1:])]
             + [{"edge_id": "1535209095:1:forward", "source": route_preview["node_ids"][0],
                 "target": route_preview["node_ids"][-1], "closed": True}],
    "original_directed_edge_ids": ["1535209095:1:forward"],
    "assumptions": ["Simulation estimates; configuration-dependent training rules.",
                    "Cruise speed cap: 20 km/h; dwell per served required stop: 15 s",
                    "Travel energy: 0.8 kWh/km; auxiliary load: 1.5 kW"],
}
save("route_editor_context.json", RouteEditorContext.model_validate(route_context).model_dump(mode="json"))
route_approval = {"contract_version": "2.0", "preview_id": "preview-m2-1",
                  "preview_revision": 3, "command_id": "command-route-1",
                  "idempotency_key": "route-key-1", "reason": "Avoid closure",
                  "target_vehicle_ids": [f"shuttle-{index:02}" for index in range(1, 6)]}
save("route_approval.json", RouteApprovalRequest.model_validate(route_approval).model_dump(mode="json"))
