"""Shared cross-client fixture gate for the M2 contract."""

import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from contracts.v2.models import DispatcherCommand, parse_message, transition_allowed, validate_command_against_snapshot
from contracts.v2.route_edit import RouteApprovalRequest, RouteEditPreview, RouteEditRequest, RouteEditorContext
from contracts.v2.sessions import SessionListResponse

FIXTURES = Path(__file__).resolve().parents[2] / "contracts" / "v2" / "fixtures"


def load(name: str) -> dict:
    return json.loads((FIXTURES / f"{name}.json").read_text(encoding="utf-8"))


@pytest.mark.parametrize("name", ["snapshot", "low_reserve_snapshot", "event_replay", "command_status", "error"])
def test_positive_shared_fixtures(name):
    value = load(name)
    assert parse_message(value).message_type == value["message_type"]


@pytest.mark.parametrize("name", ["invalid_enum", "missing_reference", "stale_scene_revision", "incompatible_major", "invalid_event_gap"])
def test_negative_shared_fixtures(name):
    with pytest.raises(ValidationError):
        parse_message(load(name))


def test_minor_version_and_additive_field_are_accepted():
    value = load("snapshot")
    value["contract_version"] = "2.1"
    value["future_metadata"] = {"ignored": True}
    assert parse_message(value).model_extra["future_metadata"] == {"ignored": True}


def test_recorded_provenance_requires_actual_frame_and_timestamp():
    value = load("snapshot")
    value["vehicles"][0]["provenance"]["kind"] = "RECORDED_SENSOR"
    with pytest.raises(ValidationError):
        parse_message(value)


def test_queued_command_cannot_claim_application():
    value = load("command_status")
    value["status"] = "QUEUED"
    with pytest.raises(ValidationError):
        parse_message(value)


def test_decision_command_requires_reason():
    command = {"contract_version": "2.0", "command_id": "command-1", "idempotency_key": "key-1", "session_id": "session-1",
               "command_type": "APPROVE_INCIDENT_OPTION", "expected_epoch": 1,
               "expected_revision": 2, "target_type": "INCIDENT", "target_id": "incident-1",
               "payload": {"candidate_id": "candidate-1"}, "reason": None}
    with pytest.raises(ValidationError):
        DispatcherCommand.model_validate(command)


def test_golden_dispatcher_command():
    assert DispatcherCommand.model_validate(load("command")).command_type == "APPROVE_INCIDENT_OPTION"


def test_session_list_golden_contract():
    listed = SessionListResponse.model_validate(load("session_list"))
    assert listed.live_count == sum(s.occupies_capacity for s in listed.sessions) == 1
    assert listed.capacity == 16
    assert listed.sessions[0].session_id == "session-001"
    assert listed.sessions[1].status == "ARCHIVED"


def test_route_edit_golden_contracts():
    draft = RouteEditRequest.model_validate(load("route_edit_request"))
    preview = RouteEditPreview.model_validate(load("route_edit_preview"))
    approval = RouteApprovalRequest.model_validate(load("route_approval"))
    assert draft.draft_revision == preview.preview_revision == approval.preview_revision
    assert preview.preview_id == approval.preview_id
    assert preview.approvable
    context = RouteEditorContext.model_validate(load("route_editor_context"))
    assert context.route_id == preview.route_id
    assert {node.node_id for node in context.nodes} == set(preview.node_ids)
    assert any(edge.closed for edge in context.edges)


def test_route_preview_cannot_claim_approval_with_failed_check():
    value = load("route_edit_preview")
    value["checks"][0]["status"] = "FAIL"
    with pytest.raises(ValidationError):
        RouteEditPreview.model_validate(value)


def test_route_edit_minor_version_and_additive_field_are_retained():
    value = load("route_edit_preview")
    value["contract_version"] = "2.1"
    value["future_metadata"] = {"revision": 5}
    parsed = RouteEditPreview.model_validate(value)
    assert parsed.model_extra["future_metadata"] == {"revision": 5}


def test_stale_target_revision_and_expired_option_fail_with_codes():
    snapshot = parse_message(load("snapshot"))
    base = {"contract_version": "2.0", "command_id": "command-1", "idempotency_key": "key-1",
            "session_id": "session-001", "command_type": "APPROVE_INCIDENT_OPTION",
            "expected_epoch": 1, "expected_revision": 1, "target_type": "INCIDENT",
            "target_id": "incident-1", "payload": {"candidate_id": "candidate-wait-1"},
            "reason": "Wait for clearance"}
    assert validate_command_against_snapshot(DispatcherCommand.model_validate(base), snapshot).code == "STALE_REVISION"
    base["expected_revision"] = 2
    snapshot.candidates[0].expires_at_tick = 199
    assert validate_command_against_snapshot(DispatcherCommand.model_validate(base), snapshot).code == "OPTION_EXPIRED"


@pytest.mark.parametrize("machine,before,after,allowed", [
    ("session", "RUNNING", "PAUSED", True),
    ("session", "ARCHIVED", "RUNNING", False),
    ("vehicle", "CHARGING", "IN_SERVICE", False),
    ("vehicle", "CHARGING", "STANDBY", True),
    ("incident", "OPTIONS_READY", "ACTION_COMMITTED", True),
    ("incident", "RESOLVED", "OPTIONS_READY", False),
    ("incident", "WAITING", "OPTIONS_READY", True),
    ("incident", "ESCALATED", "DETECTED", False),
])
def test_transition_table(machine, before, after, allowed):
    assert transition_allowed(machine, before, after) is allowed
