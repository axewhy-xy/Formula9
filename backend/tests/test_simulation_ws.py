"""
REST/WebSocket integration tests for simulation playback controls.

Author: Lau Xiang Yang, CC
Date Created: 20/09/2026
Last Modified: 20/09/2026

Description:
    This module contains integration tests for the simulation playback controls, including REST API endpoints
    and WebSocket communication. It tests the creation of simulation sessions, retrieval of session metadata,
    and the handling of playback controls such as start, pause, resume, and completion of simulation sessions.
    The tests ensure that the simulation lifecycle is correctly managed and that the WebSocket communication
    behaves as expected, including handling of invalid session IDs and proper closure of WebSocket connections.
    
"""

from collections.abc import Iterator
from pathlib import Path
import shutil
import sys
import time

import pytest
from fastapi.testclient import TestClient
from starlette.websockets import WebSocketDisconnect


BACKEND_DIR = Path(__file__).resolve().parents[1]
if str(BACKEND_DIR) not in sys.path:
    sys.path.insert(0, str(BACKEND_DIR))

from src.app import app
from src.services.session_manager import session_manager


@pytest.fixture(autouse=True)
def isolated_sessions():
    session_manager.clear()
    yield
    session_manager.clear()


@pytest.fixture
def client() -> Iterator[TestClient]:
    # A context-managed client keeps every WebSocket on the application's one
    # event loop, matching production and the per-simulation task invariant.
    with TestClient(app) as test_client:
        yield test_client


def _create_demo(client: TestClient) -> dict:
    response = client.post(
        "/api/simulations",
        # Keep integration playback fast without making the complete 80-tick
        # stream shorter than a clean CI runner's WebSocket round trip.
        json={"scenario_name": "ws test", "playback_speed": 4.0},
    )
    assert response.status_code == 201
    return response.json()


def _control(message_type: str, simulation_id: str) -> dict[str, str]:
    return {"message_type": message_type, "simulation_id": simulation_id}


def test_create_simulation_exposes_stream_metadata(client: TestClient) -> None:
    created = _create_demo(client)

    assert created["status"] == "READY"
    assert created["dataset_name"] == "demo"
    assert created["trajectory"]["sample_count"] == 80
    assert created["route_summary"]["bounds"] == {
        "min_x": 0.0,
        "min_y": 0.0,
        "max_x": 128.0,
        "max_y": 46.0,
    }
    assert created["coordinates"]["z_unit"] == "m"
    assert created["contract_version"] == "1.0"
    assert created["ego_source"] == "SIMULATION"
    assert created["alignment_mode"] == "SOURCE_LOCAL"
    assert created["transform_revision"] == "synthetic-local-v1"


def test_create_simulation_with_kitti_dataset_config(
    client: TestClient, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    sequence_id = "2011_09_26_drive_test"
    fixtures = Path(__file__).parent / "fixtures"
    oxts = tmp_path / sequence_id / "oxts"
    shutil.copytree(fixtures / "sample_oxts", oxts / "data")
    shutil.copyfile(fixtures / "timestamps.txt", oxts / "timestamps.txt")
    monkeypatch.setenv("KITTI_DATA_ROOT", str(tmp_path))

    response = client.post(
        "/api/simulations",
        json={
            "scenario_name": "KITTI fixture",
            "dataset_name": "kitti",
            "sequence_id": sequence_id,
            "playback_speed": 2.0,
        },
    )

    assert response.status_code == 201
    created = response.json()
    assert created["dataset_name"] == "kitti"
    assert created["sequence_id"] == sequence_id
    assert created["trajectory"]["sample_count"] == 6


def test_create_simulation_rejects_incomplete_dataset_config(
    client: TestClient,
) -> None:
    response = client.post(
        "/api/simulations",
        json={"dataset_name": "kitti"},
    )

    assert response.status_code == 422
    assert response.json()["error"]["code"] == "validation_error"


def test_websocket_ticks_pause_resume_and_complete(client: TestClient) -> None:
    created = _create_demo(client)
    simulation_id = created["simulation_id"]

    with client.websocket_connect(created["websocket_url"]) as websocket:
        ready = websocket.receive_json()
        metadata = websocket.receive_json()
        assert ready["message_type"] == "SESSION_READY"
        assert metadata["message_type"] == "TRAJECTORY_METADATA"

        websocket.send_json(_control("START", simulation_id))
        first = websocket.receive_json()
        assert first["message_type"] == "TICK"
        assert first["tick"] == 0

        websocket.send_json(_control("PAUSE", simulation_id))
        for _ in range(100):
            if session_manager.get(simulation_id).status == "PAUSED":
                break
            time.sleep(0.001)
        assert session_manager.get(simulation_id).status == "PAUSED"
        paused_tick = session_manager.get_session(simulation_id).current_tick
        time.sleep(0.02)
        assert session_manager.get_session(simulation_id).current_tick == paused_tick

        websocket.send_json(_control("RESUME", simulation_id))
        second = websocket.receive_json()
        assert second["message_type"] == "TICK"
        assert second["tick"] == 1

        last_tick = second
        while True:
            message = websocket.receive_json()
            if message["message_type"] == "TICK":
                assert message["tick"] == last_tick["tick"] + 1
                last_tick = message
            elif message["message_type"] == "SIMULATION_COMPLETE":
                assert message["final_tick"] == last_tick["tick"]
                break

    status_response = client.get(f"/api/simulations/{simulation_id}")
    assert status_response.status_code == 200
    assert status_response.json()["status"] == "COMPLETED"


def test_websocket_subscribers_share_one_playback_clock(client: TestClient) -> None:
    created = _create_demo(client)
    simulation_id = created["simulation_id"]

    with client.websocket_connect(created["websocket_url"]) as browser:
        browser.receive_json()
        browser.receive_json()

        with client.websocket_connect(created["websocket_url"]) as native:
            native.receive_json()
            native.receive_json()

            session = session_manager.get_session(simulation_id)
            playback_task = session.playback_task
            assert playback_task is not None
            assert len(session.subscribers) == 2
            assert all(queue.maxsize == 32 for queue in session.subscribers.values())

            browser.send_json(_control("START", simulation_id))
            browser_ticks = []
            native_ticks = []

            while True:
                browser_message = browser.receive_json()
                native_message = native.receive_json()
                assert browser_message == native_message

                if browser_message["message_type"] == "TICK":
                    browser_ticks.append(browser_message["tick"])
                    native_ticks.append(native_message["tick"])
                    continue

                assert browser_message["message_type"] == "SIMULATION_COMPLETE"
                break

            assert browser_ticks == list(range(session.stream.tick_count))
            assert native_ticks == browser_ticks
            assert session.current_tick == session.stream.tick_count
            assert session.playback_task is playback_task
            assert session.response.status == "COMPLETED"


def test_pause_from_one_subscriber_stops_shared_playback(client: TestClient) -> None:
    created = _create_demo(client)
    simulation_id = created["simulation_id"]

    with client.websocket_connect(created["websocket_url"]) as browser:
        browser.receive_json()
        browser.receive_json()

        with client.websocket_connect(created["websocket_url"]) as native:
            native.receive_json()
            native.receive_json()

            browser.send_json(_control("START", simulation_id))
            assert browser.receive_json()["tick"] == 0
            assert native.receive_json()["tick"] == 0

            native.send_json(_control("PAUSE", simulation_id))
            for _ in range(100):
                if session_manager.get(simulation_id).status == "PAUSED":
                    break
                time.sleep(0.001)

            session = session_manager.get_session(simulation_id)
            assert session.response.status == "PAUSED"
            paused_tick = session.current_tick
            time.sleep(session.stream.tick_interval * 3)
            assert session.current_tick == paused_tick

            browser.send_json(_control("RESUME", simulation_id))
            for _ in range(100):
                if session_manager.get(simulation_id).status == "RUNNING":
                    break
                time.sleep(0.001)
            assert session_manager.get(simulation_id).status == "RUNNING"

            native.send_json(_control("STOP", simulation_id))
            for _ in range(100):
                if session_manager.get(simulation_id).status == "STOPPED":
                    break
                time.sleep(0.001)
            assert session_manager.get(simulation_id).status == "STOPPED"


def test_disconnecting_native_subscriber_does_not_stop_browser_session(
    client: TestClient,
) -> None:
    created = _create_demo(client)
    simulation_id = created["simulation_id"]

    with client.websocket_connect(created["websocket_url"]) as browser:
        browser.receive_json()
        browser.receive_json()

        with client.websocket_connect(created["websocket_url"]) as native:
            native.receive_json()
            native.receive_json()
            browser.send_json(_control("START", simulation_id))
            first_browser_tick = browser.receive_json()
            first_native_tick = native.receive_json()
            assert first_browser_tick == first_native_tick

        next_browser_tick = browser.receive_json()
        assert next_browser_tick["message_type"] == "TICK"
        assert next_browser_tick["tick"] == first_browser_tick["tick"] + 1
        assert session_manager.get(simulation_id).status == "RUNNING"
        browser.send_json(_control("STOP", simulation_id))


def test_websocket_rejects_control_for_another_session(client: TestClient) -> None:
    created = _create_demo(client)
    simulation_id = created["simulation_id"]

    with client.websocket_connect(created["websocket_url"]) as websocket:
        websocket.receive_json()
        websocket.receive_json()

        websocket.send_json(_control("START", "another-session"))
        error = websocket.receive_json()

        assert error["message_type"] == "ERROR"
        assert error["error"]["error_code"] == "SIMULATION_ID_MISMATCH"
        assert error["error"]["error_details"] == {
            "expected_simulation_id": simulation_id,
            "received_simulation_id": "another-session",
        }
        assert session_manager.get(simulation_id).status == "READY"

        websocket.send_json(_control("START", simulation_id))
        assert websocket.receive_json()["message_type"] == "TICK"


def test_websocket_invalid_session_id_closes_with_policy_violation(
    client: TestClient,
) -> None:
    with pytest.raises(WebSocketDisconnect) as disconnect:
        with client.websocket_connect("/ws/simulations/not-a-session") as websocket:
            websocket.receive_json()

    assert disconnect.value.code == 1008
