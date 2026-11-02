"""
Time-slot charger bookings through the real HTTP API and session actor.

Description:
    A slot is checked by the same rules as its preview, starts on its own when its time comes,
    gives the port back when the shuttle has cleared the bay, and cannot double-book a charger.
"""
from uuid import uuid4

import pytest

from backend.tests.test_training_integration import advance, advance_until, approve, read, reserve, runtime, send  # noqa: F401


def started(rt):
    approve(rt)
    assert send(rt, "START")["status"] == "APPLIED"
    return read(rt)


def request_preview(rt, vehicle, charger, start, replaces=None):
    client, _, _, session_id = rt
    response = client.post(f"/api/v2/sessions/{session_id}/charge-schedule-previews",
        json={"vehicle_id": vehicle, "charger_id": charger, "start_tick": start, "replaces_booking_id": replaces})
    assert response.status_code == 200, response.text
    return response.json()


def schedule(rt, vehicle, charger, start, replaces=None, key=None):
    snapshot = read(rt)
    revision = next(c for c in snapshot["chargers"] if c["charger_id"] == charger)["aggregate_revision"]
    return send(rt, "SCHEDULE_CHARGE", snapshot=snapshot, target_type="CHARGER", target_id=charger, revision=revision, key=key,
                payload={"vehicle_id": vehicle, "start_tick": start, "replaces_booking_id": replaces})


def cancel(rt, booking):
    snapshot = read(rt)
    revision = next(c for c in snapshot["chargers"] if c["charger_id"] == booking["charger_id"])["aggregate_revision"]
    return send(rt, "CANCEL_CHARGE_BOOKING", snapshot=snapshot, target_type="CHARGER", target_id=booking["charger_id"],
                revision=revision, payload={"booking_id": booking["booking_id"]})


def booking_of(rt, vehicle, status=None):
    return next((b for b in read(rt)["charge_bookings"] if b["vehicle_id"] == vehicle and (status is None or b["status"] == status)), None)


def store_snapshot(rt):
    _, store, _, session_id = rt
    return store.snapshot(session_id)


def status_of(snapshot, booking_id):
    return next(b.status for b in snapshot.charge_bookings if b.booking_id == booking_id)


def test_a_booked_slot_starts_on_time_charges_and_gives_the_port_back(runtime):
    snapshot = started(runtime)
    start = snapshot["tick"] + 40
    preview = request_preview(runtime, "shuttle-05", "charger-1", start)
    assert preview["validation"] == "PASS", preview["issues"]
    assert preview["end_tick"] > start and preview["battery_after_kwh"] > 0
    assert read(runtime)["charge_bookings"] == []  # a preview reserves nothing

    assert schedule(runtime, "shuttle-05", "charger-1", start)["status"] == "APPLIED"
    booking = booking_of(runtime, "shuttle-05")
    assert booking["status"] == "SCHEDULED" and booking["origin"] == "OPERATOR"
    # Until its time comes the shuttle keeps serving and the port stays free: a booking is not occupancy
    charger = next(c for c in read(runtime)["chargers"] if c["charger_id"] == "charger-1")
    assert charger["occupied_vehicle_ids"] == []

    advance_until(runtime, lambda s: status_of(s, booking["booking_id"]) == "ACTIVE")
    now = read(runtime)
    assert now["tick"] >= start
    assert "shuttle-05" in next(c for c in now["chargers"] if c["charger_id"] == "charger-1")["occupied_vehicle_ids"]
    assert next(v for v in now["vehicles"] if v["vehicle_id"] == "shuttle-05")["service_state"] == "RETURNING_TO_DEPOT"

    # The actor makes every transition below; only the long drives and the charge are shortened.
    _, _, service, session_id = runtime
    def actor():
        return service.sessions[session_id].actor
    def nearly_there():
        shuttle = actor().shuttles[4]
        shuttle.progress = actor()._routes[shuttle.route_ID]["total_length"] - 1.0
    assert 4 in actor().set_downs  # riders get off first, as on any trip in
    actor().set_downs.pop(4)
    nearly_there()
    advance_until(runtime, lambda s: s.vehicles[4].service_state == "CHARGING", limit=100)
    assert read(runtime)["vehicles"][4]["battery_kwh"] > 0
    actor().charge_elapsed[4] = actor().definition.data["charge_s"] - 0.2
    advance_until(runtime, lambda s: s.vehicles[4].service_state == "RETURNING_TO_DEPOT", limit=100)
    assert status_of(store_snapshot(runtime), booking["booking_id"]) == "ACTIVE"  # the bay is not free until it has been cleared
    nearly_there()
    advance_until(runtime, lambda s: status_of(s, booking["booking_id"]) == "COMPLETED", limit=100)
    done = read(runtime)
    assert next(c for c in done["chargers"] if c["charger_id"] == "charger-1")["occupied_vehicle_ids"] == []
    assert next(v for v in done["vehicles"] if v["vehicle_id"] == "shuttle-05")["service_state"] == "STANDBY"


def test_a_charger_cannot_be_double_booked(runtime):
    snapshot = started(runtime)
    start = snapshot["tick"] + 400
    assert schedule(runtime, "shuttle-01", "charger-1", start)["status"] == "APPLIED"
    clash = request_preview(runtime, "shuttle-02", "charger-1", start)
    assert clash["validation"] == "FAIL" and [i["code"] for i in clash["issues"]] == ["CHARGER_CONFLICT"]
    result = schedule(runtime, "shuttle-02", "charger-1", start)
    assert result["status"] == "REJECTED" and result["failure_code"] == "CHARGER_CONFLICT"
    assert booking_of(runtime, "shuttle-02") is None
    # The other charger is fine
    assert schedule(runtime, "shuttle-02", "charger-2", start)["status"] == "APPLIED"


def test_a_shuttle_holds_one_slot_and_past_times_are_refused(runtime):
    started(runtime)
    advance(runtime, 5)
    assert request_preview(runtime, "shuttle-01", "charger-1", 0)["validation"] == "FAIL"
    start = read(runtime)["tick"] + 400
    assert schedule(runtime, "shuttle-01", "charger-1", start)["status"] == "APPLIED"
    second = schedule(runtime, "shuttle-01", "charger-2", start)
    assert second["status"] == "REJECTED" and second["failure_code"] == "CHARGER_CONFLICT"


def test_moving_a_slot_replaces_it_in_one_step_across_chargers(runtime):
    start = started(runtime)["tick"] + 400
    assert schedule(runtime, "shuttle-01", "charger-1", start)["status"] == "APPLIED"
    original = booking_of(runtime, "shuttle-01")
    assert schedule(runtime, "shuttle-01", "charger-2", start + 200, replaces=original["booking_id"])["status"] == "APPLIED"
    bookings = [b for b in read(runtime)["charge_bookings"] if b["vehicle_id"] == "shuttle-01"]
    assert sorted((b["charger_id"], b["status"]) for b in bookings) == [("charger-1", "CANCELLED"), ("charger-2", "SCHEDULED")]
    # charger-1 is free for someone else again
    assert request_preview(runtime, "shuttle-02", "charger-1", start)["validation"] != "FAIL"


def test_cancelling_frees_the_slot_and_a_started_booking_cannot_be_cancelled(runtime):
    start = started(runtime)["tick"] + 400
    schedule(runtime, "shuttle-01", "charger-1", start)
    booking = booking_of(runtime, "shuttle-01")
    assert cancel(runtime, booking)["status"] == "APPLIED"
    assert booking_of(runtime, "shuttle-01")["status"] == "CANCELLED"
    assert schedule(runtime, "shuttle-02", "charger-1", start)["status"] == "APPLIED"

    soon = read(runtime)["tick"] + 2
    schedule(runtime, "shuttle-03", "charger-3", soon)
    advance_until(runtime, lambda s: any(b.vehicle_id == "shuttle-03" and b.status == "ACTIVE" for b in s.charge_bookings))
    refused = cancel(runtime, booking_of(runtime, "shuttle-03", "ACTIVE"))
    assert refused["status"] == "REJECTED" and refused["failure_code"] == "INVALID_TRANSITION"


def test_retrying_a_schedule_command_has_one_effect(runtime):
    start = started(runtime)["tick"] + 400
    key = uuid4().hex
    first = schedule(runtime, "shuttle-01", "charger-1", start, key=key)
    snapshot = read(runtime)
    revision_before = next(c for c in snapshot["chargers"] if c["charger_id"] == "charger-1")["aggregate_revision"]
    client, _, _, session_id = runtime
    body = {"contract_version": "2.0", "command_id": uuid4().hex, "idempotency_key": key, "session_id": session_id,
            "command_type": "SCHEDULE_CHARGE", "expected_epoch": snapshot["session_epoch"], "expected_revision": revision_before - 1,
            "target_type": "CHARGER", "target_id": "charger-1", "reason": "Authored integration exercise decision",
            "payload": {"vehicle_id": "shuttle-01", "start_tick": start, "replaces_booking_id": None}}
    retry = client.post(f"/api/v2/sessions/{session_id}/commands", json=body)
    assert retry.status_code == 202 and retry.json()["command_id"] == first["command_id"]
    assert len(read(runtime)["charge_bookings"]) == 1


def test_charge_now_joins_the_schedule_and_replaces_the_shuttles_own_slot(runtime):
    start = started(runtime)["tick"] + 2000
    assert schedule(runtime, "shuttle-05", "charger-2", start)["status"] == "APPLIED"
    assert reserve(runtime)["status"] == "APPLIED"
    bookings = [b for b in read(runtime)["charge_bookings"] if b["vehicle_id"] == "shuttle-05"]
    assert {(b["origin"], b["status"]) for b in bookings} == {("OPERATOR", "CANCELLED"), ("CHARGE_NOW", "ACTIVE")}


def test_charge_now_is_not_offered_on_a_charger_another_shuttle_has_booked(runtime):
    snapshot = started(runtime)
    option = next(o for o in snapshot["charge_options"] if o["vehicle_id"] == "shuttle-05" and o["action"] == "CHARGE_NOW")
    charger = option["charger_id"]
    assert schedule(runtime, "shuttle-01", charger, snapshot["tick"] + 2)["status"] == "APPLIED"
    offers = [o for o in read(runtime)["charge_options"] if o["vehicle_id"] == "shuttle-05" and o["action"] == "CHARGE_NOW"]
    assert all(o["charger_id"] != charger for o in offers)


def test_slots_are_refused_before_the_session_runs(runtime):
    assert request_preview(runtime, "shuttle-01", "charger-1", 100)["validation"] == "FAIL"
    assert schedule(runtime, "shuttle-01", "charger-1", 100)["status"] == "REJECTED"


def test_a_slot_whose_port_stays_busy_past_its_window_is_missed(runtime):
    _, _, service, session_id = runtime
    started(runtime)
    # Shuttle-05 takes the port by charge-now; a slot booked later cannot overlap its estimate, so
    # shorten that estimate to stand for a charge that overruns.
    assert reserve(runtime)["status"] == "APPLIED"
    held = booking_of(runtime, "shuttle-05", "ACTIVE")
    actor = service.sessions[session_id].actor
    next(b for b in actor.domain.charge_bookings if b.booking_id == held["booking_id"]).end_tick = read(runtime)["tick"] + 1
    advance(runtime, 2)
    tick = read(runtime)["tick"]
    assert read(runtime)["charge_bookings"][-1]["end_tick"] > tick  # the overrun stays on the schedule
    actor = service.sessions[session_id].actor
    # Book against the moved estimate directly: the actor must still never put two shuttles on one port
    from contracts.v2.models import ChargeBooking
    actor.domain.charge_bookings.append(ChargeBooking(booking_id="late", revision=1, vehicle_id="shuttle-01", charger_id=held["charger_id"],
        status="SCHEDULED", origin="OPERATOR", start_tick=tick, end_tick=tick + 5))
    advance(runtime, 20)
    now = read(runtime)
    late = next(b for b in now["charge_bookings"] if b["booking_id"] == "late")
    assert late["status"] == "MISSED" and late["status_reason"] == "PORT_BUSY"
    assert next(c for c in now["chargers"] if c["charger_id"] == held["charger_id"])["occupied_vehicle_ids"] == ["shuttle-05"]


def test_a_shuttle_that_cannot_wait_for_its_slot_is_warned_before_booking(runtime):
    snapshot = started(runtime)
    far = snapshot["tick"] + 70000
    preview = request_preview(runtime, "shuttle-05", "charger-1", far)
    assert preview["validation"] == "FAIL" and "NO_FEASIBLE_ROUTE" in [i["code"] for i in preview["issues"]]
    assert preview["battery_at_start_kwh"] < next(v for v in snapshot["vehicles"] if v["vehicle_id"] == "shuttle-05")["battery_kwh"]


def test_bookings_survive_a_backend_restart(tmp_path):
    from fastapi.testclient import TestClient
    from src.api.routes.training_v2 import get_actor_id, get_training_store
    from src.app import app
    from src.persistence.training_store import TrainingStore
    from src.services.integrated_actor import SCENARIO_SELECTOR
    database, previous = tmp_path/"restart.sqlite", dict(app.dependency_overrides)
    try:
        with TrainingStore(database) as store:
            app.dependency_overrides[get_training_store] = lambda: store
            app.dependency_overrides[get_actor_id] = lambda: "integration-operator"
            with TestClient(app) as client:
                session_id = client.post("/api/v2/sessions", headers={"Idempotency-Key": "booking-restart"},
                    json={"scenario_revision_id": SCENARIO_SELECTOR, "seed": 1000}).json()["session_id"]
                rt = (client, store, app.state.training_services[store], session_id)
                start = started(rt)["tick"] + 400
                assert schedule(rt, "shuttle-01", "charger-1", start)["status"] == "APPLIED"
                booking = booking_of(rt, "shuttle-01")
        with TrainingStore(database) as store:
            app.dependency_overrides[get_training_store] = lambda: store
            with TestClient(app) as client:
                rt = (client, store, app.state.training_services[store], session_id)
                assert booking_of(rt, "shuttle-01")["booking_id"] == booking["booking_id"]
                assert booking_of(rt, "shuttle-01")["status"] == "SCHEDULED"
    finally:
        app.dependency_overrides.clear()
        app.dependency_overrides.update(previous)


@pytest.mark.real_charge_rate
def test_a_lower_battery_books_a_longer_slot_and_it_charges_to_full(runtime):
    snapshot = started(runtime)
    start = snapshot["tick"] + 2
    low = request_preview(runtime, "shuttle-05", "charger-1", start)
    fuller = request_preview(runtime, "shuttle-01", "charger-1", start)
    charge_ticks = lambda p, vehicle: p["end_tick"] - p["start_tick"] - next(
        f["next_depot_eta_s"] for f in snapshot["energy_forecasts"] if f["vehicle_id"] == vehicle) * 20
    assert charge_ticks(low, "shuttle-05") > charge_ticks(fuller, "shuttle-01") * 1.1
    assert low["battery_after_kwh"] == pytest.approx(40, abs=0.5)
    assert 6 * 60 < low["out_of_service_s"] - 60 - next(f["next_depot_eta_s"] for f in snapshot["energy_forecasts"] if f["vehicle_id"] == "shuttle-05") < 9 * 60

    assert schedule(runtime, "shuttle-05", "charger-1", start)["status"] == "APPLIED"
    booking_id = booking_of(runtime, "shuttle-05")["booking_id"]
    advance_until(runtime, lambda s: status_of(s, booking_id) == "ACTIVE")
    advance(runtime, 2)
    active = booking_of(runtime, "shuttle-05", "ACTIVE")
    # It says it is dropping its riders, and where, and publishes the way to the charger
    assert active["phase"] == "DROPPING_PASSENGERS" and active["set_down_stop_id"]
    assert len(active["trip_path_m"]) > 1

    _, _, service, session_id = runtime
    def actor():
        return service.sessions[session_id].actor
    actor().set_downs.pop(4)
    advance(runtime, 2)
    assert booking_of(runtime, "shuttle-05", "ACTIVE")["phase"] == "DRIVING_TO_CHARGER"
    shuttle = actor().shuttles[4]
    shuttle.progress = actor()._routes[shuttle.route_ID]["total_length"] - 1.0
    advance_until(runtime, lambda s: s.vehicles[4].service_state == "CHARGING", limit=100)
    charging = booking_of(runtime, "shuttle-05", "ACTIVE")
    assert charging["phase"] == "CHARGING" and charging["trip_path_m"] == []
    before = read(runtime)["vehicles"][4]["battery_kwh"]
    advance(runtime, 40)
    now = read(runtime)["vehicles"][4]["battery_kwh"]
    assert now - before == pytest.approx(40 * 0.05 * 50 * 5.5 * 0.9 / 3600, abs=0.05)
    assert read(runtime)["vehicles"][4]["service_state"] == "CHARGING"
    actor().shuttles[4].battery = 99.99
    advance_until(runtime, lambda s: s.vehicles[4].service_state == "RETURNING_TO_DEPOT", limit=60)
    assert booking_of(runtime, "shuttle-05", "ACTIVE")["phase"] == "LEAVING_BAY"


def charge_to_full_and_leave(runtime, vehicle_index, vehicle_id):
    _, _, service, session_id = runtime
    def actor():
        return service.sessions[session_id].actor
    actor().set_downs.pop(vehicle_index, None)
    shuttle = actor().shuttles[vehicle_index]
    shuttle.progress = actor()._routes[shuttle.route_ID]["total_length"] - 1.0
    advance_until(runtime, lambda s: s.vehicles[vehicle_index].service_state == "CHARGING", limit=100)
    actor().shuttles[vehicle_index].battery = 99.99
    advance_until(runtime, lambda s: s.vehicles[vehicle_index].service_state == "RETURNING_TO_DEPOT", limit=60)
    return actor


@pytest.mark.real_charge_rate
def test_a_charged_shuttle_gives_the_port_back_and_rejoins_its_route(runtime):
    snapshot = started(runtime)
    start = snapshot["tick"] + 2
    route_id = next(v["route_id"] for v in snapshot["vehicles"] if v["vehicle_id"] == "shuttle-05")
    assert schedule(runtime, "shuttle-05", "charger-1", start)["status"] == "APPLIED"
    booking_id = booking_of(runtime, "shuttle-05")["booking_id"]
    advance_until(runtime, lambda s: status_of(s, booking_id) == "ACTIVE")
    actor = charge_to_full_and_leave(runtime, 4, "shuttle-05")
    charger = lambda: next(c for c in read(runtime)["chargers"] if c["charger_id"] == "charger-1")
    assert "shuttle-05" in charger()["occupied_vehicle_ids"]  # still in the bay

    # Out of the bay: the port is free for the next shuttle, with the way back on the map
    advance_until(runtime, lambda s: s.vehicles[4].service_state == "IN_SERVICE", limit=400)
    assert charger()["occupied_vehicle_ids"] == []
    returning = booking_of(runtime, "shuttle-05", "ACTIVE")
    assert returning["phase"] == "RETURNING_TO_ROUTE" and len(returning["trip_path_m"]) > 1
    assert returning["trip_path_m"][-1] == pytest.approx([actor()._route_evaluation(route_id, 0.0).x, actor()._route_evaluation(route_id, 0.0).y])
    assert 4 not in actor().loop_started  # the drive back is not a loop of the route

    # It really drives: no jump to the route
    previous = read(runtime)["vehicles"][4]["position"]
    advance(runtime, 20)
    now = read(runtime)["vehicles"][4]["position"]
    moved = ((now["x_m"] - previous["x_m"]) ** 2 + (now["y_m"] - previous["y_m"]) ** 2) ** 0.5
    assert 0 < moved < 20 * 0.05 * actor().shuttles[4].cruise_vel * 1.5

    shuttle = actor().shuttles[4]
    shuttle.progress = actor()._routes[shuttle.route_ID]["total_length"] - 1.0
    advance_until(runtime, lambda s: status_of(s, booking_id) == "COMPLETED", limit=100)
    done = read(runtime)
    back = next(v for v in done["vehicles"] if v["vehicle_id"] == "shuttle-05")
    assert back["service_state"] == "DWELLING" and back["route_id"] == route_id
    assert actor().shuttles[4].route_ID == route_id and actor().shuttles[4].progress == 0.0
    # Then a normal loop, battery near full
    advance_until(runtime, lambda s: s.vehicles[4].service_state == "IN_SERVICE", limit=800)
    advance(runtime, 20)
    assert actor().shuttles[4].progress > 0 and read(runtime)["vehicles"][4]["battery_kwh"] > 39


@pytest.mark.real_charge_rate
def test_the_port_can_be_booked_while_the_last_shuttle_drives_back(runtime):
    snapshot = started(runtime)
    assert schedule(runtime, "shuttle-05", "charger-1", snapshot["tick"] + 2)["status"] == "APPLIED"
    booking_id = booking_of(runtime, "shuttle-05")["booking_id"]
    advance_until(runtime, lambda s: status_of(s, booking_id) == "ACTIVE")
    charge_to_full_and_leave(runtime, 4, "shuttle-05")
    advance_until(runtime, lambda s: s.vehicles[4].service_state == "IN_SERVICE", limit=400)
    preview = request_preview(runtime, "shuttle-04", "charger-1", read(runtime)["tick"] + 2)
    assert not [i for i in preview["issues"] if i["code"] == "CHARGER_CONFLICT"]


@pytest.mark.real_charge_rate
def test_a_charging_trip_never_locks_up_with_traffic_on_the_same_road(runtime):
    # No lanes are modelled: nose to nose, neither shuttle could ever move again
    snapshot = started(runtime)
    assert schedule(runtime, "shuttle-05", "charger-1", snapshot["tick"] + 2)["status"] == "APPLIED"
    booking_id = booking_of(runtime, "shuttle-05")["booking_id"]
    advance_until(runtime, lambda s: status_of(s, booking_id) == "ACTIVE")
    _, _, service, session_id = runtime
    actor = service.sessions[session_id].actor
    tripping, traffic, other = actor.shuttles[4], actor.shuttles[2], actor.shuttles[1]
    assert tripping.route_ID.startswith("depot-path-")
    assert actor._conflicts(tripping, tripping.progress, traffic, traffic.progress) is False
    # Ordinary traffic still keeps its distance
    other.route_ID, other.progress = traffic.route_ID, traffic.progress
    assert actor._conflicts(traffic, traffic.progress, other, other.progress) is True


def test_charging_trips_stay_on_real_roads_and_keep_to_one_way_streets():
    from src.services.integrated_actor import get_workflow_definition
    data = get_workflow_definition().data
    interchange = data["depots"][0]
    bus_road = [3083.42, -2264.83]
    for depot in data["depots"]:
        for route, way_in in depot["connectors"].items():
            way_out = depot["exits"][route]
            assert way_out[-1] == way_in[0]  # back at the start of the loop
            assert way_out != list(reversed(way_in))  # one-way streets: a way of its own
            x, y = depot["position_m"]
            # No long straight line to the depot. Saint Michael's sits at the edge of the map data,
            # so the nearest road it can leave by is further off.
            limit = 65 if depot is interchange else 130
            for end in (way_in[-1], way_out[0]):
                assert ((end[0] - x) ** 2 + (end[1] - y) ** 2) ** 0.5 < limit
    # In by the interchange's own bus road
    assert any(abs(p[0] - bus_road[0]) < 3 and abs(p[1] - bus_road[1]) < 3 for p in interchange["connectors"]["route-peer-c"])


def test_chargers_publish_where_they_are(runtime):
    snapshot = started(runtime)
    from src.services.integrated_actor import get_workflow_definition
    bays = get_workflow_definition().bay_positions
    for charger in snapshot["chargers"]:
        assert [charger["position"]["x_m"], charger["position"]["y_m"]] == bays[charger["charger_id"]]


@pytest.mark.real_charge_rate
def test_charge_now_also_goes_back_to_its_route(runtime):
    started(runtime)
    route_id = next(v["route_id"] for v in read(runtime)["vehicles"] if v["vehicle_id"] == "shuttle-05")
    assert reserve(runtime)["status"] == "APPLIED"
    assert booking_of(runtime, "shuttle-05", "ACTIVE")["origin"] == "CHARGE_NOW"
    actor = charge_to_full_and_leave(runtime, 4, "shuttle-05")
    advance_until(runtime, lambda s: s.vehicles[4].service_state == "IN_SERVICE", limit=400)
    shuttle = actor().shuttles[4]
    shuttle.progress = actor()._routes[shuttle.route_ID]["total_length"] - 1.0
    advance_until(runtime, lambda s: s.vehicles[4].service_state == "DWELLING", limit=100)
    assert actor().shuttles[4].route_ID == route_id and booking_of(runtime, "shuttle-05", "ACTIVE") is None


@pytest.mark.real_charge_rate
def test_a_safety_return_also_goes_back_to_its_route():
    from backend.tests.test_depots import low_battery_kwh, run_until, running_actor
    actor = running_actor()
    shuttle = actor.shuttles[1]
    route_id = shuttle.route_ID
    shuttle.battery = (low_battery_kwh(actor, 1) - 0.3) * 100 / actor.BATTERY_CAPACITY_KWH
    run_until(actor, 1, "SAFETY_RETURN")
    run_until(actor, 1, "CHARGING_STARTED")
    shuttle.battery = 99.99
    run_until(actor, 1, "RETURNED_TO_ROUTE")
    assert shuttle.route_ID == route_id and actor.modes[1] == "DWELLING"
    assert not any(actor.domain.vehicles[1].vehicle_id in c.occupied_vehicle_ids for c in actor.domain.chargers)


@pytest.mark.real_charge_rate
def test_the_drive_back_survives_a_backend_restart(tmp_path, monkeypatch):
    from fastapi.testclient import TestClient
    from backend.tests.test_training_integration import manual_clock
    from src.api.routes.training_v2 import get_actor_id, get_training_store
    from src.app import app
    from src.persistence.training_store import TrainingStore
    from src.services.integrated_actor import SCENARIO_SELECTOR
    from src.services.training_sessions import TrainingSessionService
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)
    database, previous = tmp_path/"restart.sqlite", dict(app.dependency_overrides)
    try:
        with TrainingStore(database) as store:
            app.dependency_overrides[get_training_store] = lambda: store
            app.dependency_overrides[get_actor_id] = lambda: "integration-operator"
            with TestClient(app) as client:
                session_id = client.post("/api/v2/sessions", headers={"Idempotency-Key": "return-restart"},
                    json={"scenario_revision_id": SCENARIO_SELECTOR, "seed": 1000}).json()["session_id"]
                rt = (client, store, app.state.training_services[store], session_id)
                start = started(rt)["tick"] + 2
                assert schedule(rt, "shuttle-05", "charger-1", start)["status"] == "APPLIED"
                booking_id = booking_of(rt, "shuttle-05")["booking_id"]
                advance_until(rt, lambda s: status_of(s, booking_id) == "ACTIVE")
                charge_to_full_and_leave(rt, 4, "shuttle-05")
                advance_until(rt, lambda s: s.vehicles[4].service_state == "IN_SERVICE", limit=400)
        with TrainingStore(database) as store:
            app.dependency_overrides[get_training_store] = lambda: store
            with TestClient(app) as client:
                rt = (client, store, app.state.training_services[store], session_id)
                actor = rt[2].sessions[session_id].actor
                shuttle = actor.shuttles[4]
                assert actor.modes[4] == "IN_SERVICE" and shuttle.route_ID.startswith("depot-path-")
                assert booking_of(rt, "shuttle-05", "ACTIVE")["phase"] == "RETURNING_TO_ROUTE"
                status = read(rt)["session"]["status"]
                assert status in {"RUNNING", "PAUSED"}, status
                if status == "PAUSED":
                    assert send(rt, "RESUME")["status"] == "APPLIED"
                # Commands and ticks replace the actor with a staged copy, so take the current one
                shuttle = rt[2].sessions[session_id].actor.shuttles[4]
                shuttle.progress = rt[2].sessions[session_id].actor._routes[shuttle.route_ID]["total_length"] - 1.0
                advance_until(rt, lambda s: status_of(s, booking_id) == "COMPLETED", limit=100)
                assert not rt[2].sessions[session_id].actor.shuttles[4].route_ID.startswith("depot-path-")
    finally:
        app.dependency_overrides.clear()
        app.dependency_overrides.update(previous)


def test_scenarios_that_cannot_run_a_slot_refuse_to_book_one(tmp_path, monkeypatch):
    from fastapi.testclient import TestClient
    from backend.tests.test_training_integration import manual_clock
    from src.api.routes.training_v2 import get_actor_id, get_training_store
    from src.app import app
    from src.persistence.training_store import TrainingStore
    from src.services.training_sessions import SCENARIO_REVISION, TrainingSessionService
    monkeypatch.setattr(TrainingSessionService, "_run", manual_clock)
    previous = dict(app.dependency_overrides)
    try:
        with TrainingStore(tmp_path/"other.sqlite") as store:
            app.dependency_overrides[get_training_store] = lambda: store
            app.dependency_overrides[get_actor_id] = lambda: "integration-operator"
            with TestClient(app) as client:
                session_id = client.post("/api/v2/sessions", headers={"Idempotency-Key": "other-scenario"},
                    json={"scenario_revision_id": SCENARIO_REVISION, "seed": 1000}).json()["session_id"]
                rt = (client, store, app.state.training_services[store], session_id)
                preview = client.post(f"/api/v2/sessions/{session_id}/charge-schedule-previews",
                    json={"vehicle_id": "shuttle-01", "charger_id": "charger-1", "start_tick": 10})
                assert preview.status_code == 409 or preview.status_code == 400, preview.status_code
                snapshot = read(rt)
                assert snapshot["charge_bookings"] == []
                command = client.post(f"/api/v2/sessions/{session_id}/commands", json={
                    "contract_version": "2.0", "command_id": str(uuid4()), "idempotency_key": str(uuid4()), "session_id": session_id,
                    "command_type": "SCHEDULE_CHARGE", "expected_epoch": snapshot["session_epoch"], "expected_revision": 1,
                    "target_type": "CHARGER", "target_id": "charger-1", "payload": {"vehicle_id": "shuttle-01", "start_tick": 10},
                    "reason": "should not book"})
                assert command.status_code in {400, 409}, command.text
                assert read(rt)["charge_bookings"] == []
    finally:
        app.dependency_overrides.clear()
        app.dependency_overrides.update(previous)


def yard_actor():
    from backend.tests.test_depots import running_actor
    actor = running_actor()
    for shuttle in actor.shuttles:  # only the two under test move
        actor.modes[shuttle.ID] = "STANDBY"
    return actor


def drive_in(actor, shuttle, charger_id, metres_short_of_yard):
    from src.schemas.shuttle_data import ShuttleState
    port = next(c for c in actor.domain.chargers if c.charger_id == charger_id)
    port.occupied_vehicle_ids.append(actor.domain.vehicles[shuttle.ID].vehicle_id)
    actor._head_for_bay(shuttle, actor._depot_points(shuttle, charger_id))
    actor._set_mode(shuttle, "RETURNING_TO_DEPOT")
    shuttle.state, shuttle.curr_vel = ShuttleState.ROUTE_LOOP, shuttle.cruise_vel
    shuttle.progress = actor._yard_entry_m(shuttle) - metres_short_of_yard
    shuttle.position = actor._route_evaluation(shuttle.route_ID, shuttle.progress)


def in_bay_ready_to_leave(actor, shuttle, charger_id):
    from src.schemas.shuttle_data import Vector2D
    port = next(c for c in actor.domain.chargers if c.charger_id == charger_id)
    port.occupied_vehicle_ids.append(actor.domain.vehicles[shuttle.ID].vehicle_id)
    bay = actor.definition.bay_positions[charger_id]
    shuttle.position = Vector2D(x=bay[0], y=bay[1])
    actor._begin_parking(shuttle, charger_id)


@pytest.mark.parametrize("rate", [None, "real"])
def test_a_shuttle_driving_in_waits_outside_the_yard_for_one_driving_out(rate, request):
    from src.services.integrated_actor import get_workflow_definition
    data = get_workflow_definition().data
    saved = data["charge_time_scale"]
    data["charge_time_scale"] = 5.5 if rate else None
    try:
        actor = yard_actor()
        coming, going = actor.shuttles[3], actor.shuttles[4]
        drive_in(actor, coming, "charger-2", 5)
        in_bay_ready_to_leave(actor, going, "charger-1")
        wait_at = actor._yard_entry_m(coming)
        waited, closest = 0, 1e9
        for _ in range(3000):
            actor.tick()
            if actor.modes[coming.ID] == "CHARGING":
                break
            if actor._in_yard(going):
                assert coming.progress <= wait_at + 1e-6, "it must not enter while the other is moving in the yard"
                waited += coming.progress == wait_at
                closest = min(closest, ((coming.position.x-going.position.x)**2 + (coming.position.y-going.position.y)**2)**0.5)
        assert actor.modes[coming.ID] == "CHARGING", "and then it gets to its charger: no deadlock"
        assert waited > 0
        assert closest >= 10, "they never meet in the yard"
        assert actor.modes[going.ID] == ("IN_SERVICE" if rate else "STANDBY")
    finally:
        data["charge_time_scale"] = saved


def test_a_shuttle_stays_in_its_bay_until_the_one_driving_in_has_stopped():
    actor = yard_actor()
    coming, going = actor.shuttles[3], actor.shuttles[4]
    drive_in(actor, coming, "charger-2", -5)  # already past the wait
    in_bay_ready_to_leave(actor, going, "charger-1")
    for _ in range(3000):
        actor.tick()
        if actor.modes[coming.ID] in {"QUEUED_FOR_CHARGE", "CHARGING"}:
            break
        assert going.progress == 0, "the way out stays shut while the other is moving in the yard"
    assert actor.modes[coming.ID] in {"QUEUED_FOR_CHARGE", "CHARGING"}
    for _ in range(3000):
        actor.tick()
        if actor.modes[going.ID] == "STANDBY":
            break
    assert actor.modes[going.ID] == "STANDBY", "then it leaves: no deadlock"


def test_two_shuttles_arriving_together_enter_the_yard_one_at_a_time():
    actor = yard_actor()
    first, second = actor.shuttles[3], actor.shuttles[4]
    drive_in(actor, first, "charger-1", 20)
    drive_in(actor, second, "charger-2", 20)
    for _ in range(3000):
        actor.tick()
        assert not (actor._in_yard(first) and actor._in_yard(second))
        if {actor.modes[first.ID], actor.modes[second.ID]} <= {"QUEUED_FOR_CHARGE", "CHARGING"}:
            break
    assert {actor.modes[first.ID], actor.modes[second.ID]} <= {"QUEUED_FOR_CHARGE", "CHARGING"}
