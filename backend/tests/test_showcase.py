"""The showcase session (Tools/showcase_backend.py): its world events stand on the exercise's roads and every
kind plays out in front of the shuttles within its first minutes."""

import pytest

from backend.tests.test_training_integration import advance, approve, read, runtime, send  # noqa: F401
from src.services.incident_scenarios.world_events import EVENT_TYPES
from src.services.integrated_actor import get_workflow_definition
from Tools.showcase_backend import install_showcase


@pytest.fixture
def showcase(monkeypatch):
    """Installs the showcase on the shared workflow definition and puts the catalogue back afterwards."""
    catalog = get_workflow_definition().incidents.simulated_catalog
    monkeypatch.setattr(catalog, "timeline", catalog.timeline)
    monkeypatch.setattr(catalog, "policy", catalog.policy)
    return install_showcase(get_workflow_definition())


def test_the_showcase_schedules_every_kind_of_world_event_and_brings_random_ones_forward(showcase):
    assert sorted(event.event_type for event in showcase) == sorted(EVENT_TYPES)
    catalog = get_workflow_definition().incidents.simulated_catalog
    assert catalog.timeline == showcase
    assert catalog.policy["max_interval_s"] <= 90
    # The rest of the random policy is the scenario's own
    assert catalog.policy["max_active_incidents"] >= 1 and "shuttle_impact_probability" in catalog.policy


def test_a_showcase_timeline_off_the_network_is_refused(tmp_path, monkeypatch):
    catalog = get_workflow_definition().incidents.simulated_catalog
    monkeypatch.setattr(catalog, "timeline", catalog.timeline)
    bad = tmp_path / "showcase.json"
    bad.write_text('{"schema_version": 1, "timeline": [{"event_id": "x", "event_type": "COLLISION", '
                   '"location": {"edge_id": "nowhere"}, "at_s": 1, "duration_s": 10}]}', encoding="utf-8")
    with pytest.raises(ValueError, match="unknown edge"):
        install_showcase(get_workflow_definition(), bad)


@pytest.mark.traffic
def test_every_showcase_event_plays_out_in_front_of_the_shuttles(runtime, showcase):
    client, store, service, session_id = runtime
    approve(runtime)
    assert send(runtime, "START", key="start")["status"] == "APPLIED"
    actor = service.sessions[session_id].actor
    last = max(event.at_s for event in showcase)
    advance(runtime, round((last + 2) / actor.STEP_S))
    actor = service.sessions[session_id].actor
    snapshot = read(runtime)
    # Every one was placed, the pedestrian's already over and settled
    assert {f"scheduled-{event.event_id}" for event in showcase} <= {i["incident_id"] for i in snapshot["incidents"]}
    live = {record["incident_id"].removeprefix("scheduled-"): record for record in actor.random_incidents}
    by_type = {event.event_type: event.event_id for event in showcase}
    # The closure asks a shuttle to decide, and a detour round it is on offer
    assert live[by_type["ROAD_CLOSURE"]]["requires_guidance"]
    assert any(c["action"] == "ROAD_DETOUR" and c["validation"] == "PASS" for c in snapshot["candidates"])
    # The traffic plays the rest out: stuck lights, a broken-down vehicle and a crash
    assert showcase[[e.event_type for e in showcase].index("SIGNAL_FAULT")].node_id in actor.traffic.signal_faults
    assert {by_type["BREAKDOWN"], by_type["COLLISION"]} <= set(actor.traffic.pins)
