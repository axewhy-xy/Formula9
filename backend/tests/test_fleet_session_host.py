"""Exercise the real fleet scheduler with concurrent presentation consumers."""

import asyncio
from dataclasses import FrozenInstanceError
import json

import pytest

from contracts.v2.models import parse_message
from src.schemas.shuttle_data import Command, CommandType
from src.services.asset_loader import load_routes_from_json
from src.services.fleet_session_host import FleetSessionHost


async def next_publication(queue):
    return await asyncio.wait_for(queue.get(), timeout=2.0)


def test_shared_open_route_is_rejected_before_registering_a_hosted_session():
    async def exercise():
        host = FleetSessionHost()
        try:
            routes = [{"route_ID": "open", "waypoints": [{"x": 0, "y": 0}, {"x": 100, "y": 0}]}]
            with pytest.raises(ValueError, match="Open route 'open'"):
                host.create_session("invalid", seed=1000, routes=routes)
            assert not host._sessions
            publication = host.create_session("invalid", seed=1000, routes=load_routes_from_json())
            assert publication.command_tick == 0
            assert len(host._sessions) == 1
        finally:
            await host.close()
    asyncio.run(exercise())


def test_two_clients_share_one_actor_clock_and_immutable_publication():
    async def exercise():
        host = FleetSessionHost()
        try:
            initial = host.create_session("paired", seed=1000, routes=load_routes_from_json())
            task = host._sessions["paired"].task
            first_id, first = host.subscribe("paired")
            _, second = host.subscribe("paired")
            assert first.get_nowait() is second.get_nowait() is initial
            for _ in range(10):
                assert host.latest("paired") is initial
            with pytest.raises(ValueError, match="already exists"):
                host.create_session("paired", seed=1000, routes=load_routes_from_json())
            for boundary in (2, 4, 6):
                publication = await next_publication(first)
                assert second.get_nowait() is publication
                assert publication.command_tick == boundary
                snapshot = parse_message(json.loads(publication.snapshot_json))
                assert snapshot.tick == boundary
                assert len(snapshot.vehicles) == 5
                with pytest.raises(FrozenInstanceError):
                    publication.snapshot_json = "changed"
            host.unsubscribe("paired", first_id)
            assert host._sessions["paired"].task is task
            assert not task.done()
        finally:
            await host.close()
        assert task.done()
    asyncio.run(exercise())


def test_slow_client_coalesces_without_delaying_the_session_or_peer():
    async def exercise():
        host = FleetSessionHost()
        try:
            host.create_session("coalesced", seed=1000, routes=load_routes_from_json())
            _, slow = host.subscribe("coalesced")
            fast_id, fast = host.subscribe("coalesced")
            fast.get_nowait()
            for _ in range(3):
                latest = await next_publication(fast)
            assert slow.qsize() == 1
            assert slow.get_nowait() is latest
            host.unsubscribe("coalesced", fast_id)
            # A session continues when the last observer leaves, and reconnects
            # reuse its task rather than starting an independent clock.
            task = host._sessions["coalesced"].task
            for subscriber_id in list(host._sessions["coalesced"].subscribers):
                host.unsubscribe("coalesced", subscriber_id)

            async def wait_for_unobserved_publication():
                while host.latest("coalesced").command_tick <= latest.command_tick:
                    await asyncio.sleep(0.01)

            await asyncio.wait_for(wait_for_unobserved_publication(), timeout=2.0)
            _, reconnected = host.subscribe("coalesced")
            baseline = reconnected.get_nowait()
            assert baseline.command_tick > latest.command_tick
            following = await next_publication(reconnected)
            assert following.command_tick == baseline.command_tick + 2
            assert host._sessions["coalesced"].task is task
        finally:
            await host.close()
    asyncio.run(exercise())


def test_paused_and_reset_sessions_still_publish_at_control_boundaries():
    async def exercise():
        host = FleetSessionHost()
        try:
            host.create_session("controls", seed=1000, routes=load_routes_from_json())
            for kind, boundary in [(CommandType.PAUSE, 1), (CommandType.RESUME, 6), (CommandType.RESET, 8)]:
                host.enqueue_command("controls", Command(type=kind, triggered_tick=boundary))
            _, queue = host.subscribe("controls")
            queue.get_nowait()
            for boundary, tick, epoch, status in [(2, 0, 1, "PAUSED"), (4, 0, 1, "PAUSED"),
                                                   (6, 1, 1, "RUNNING"), (8, 0, 2, "RUNNING")]:
                publication = await next_publication(queue)
                snapshot = parse_message(json.loads(publication.snapshot_json))
                assert publication.command_tick == boundary
                assert (snapshot.tick, snapshot.session_epoch, snapshot.session.status) == (tick, epoch, status)
        finally:
            await host.close()
    asyncio.run(exercise())


def test_actor_failure_wakes_observers_and_rejects_further_controls(monkeypatch):
    async def exercise():
        host = FleetSessionHost()
        try:
            host.create_session("failed", seed=1000, routes=load_routes_from_json())
            _, queue = host.subscribe("failed")
            queue.get_nowait()

            def fail():
                raise RuntimeError("private implementation details")

            monkeypatch.setattr(host._sessions["failed"].actor, "tick", fail)
            failure = await next_publication(queue)
            assert failure.error_code == "ACTOR_FAILED"
            assert failure.snapshot_json is None
            with pytest.raises(RuntimeError, match="failed"):
                host.enqueue_command("failed", Command(type=CommandType.PAUSE, triggered_tick=0))
        finally:
            await host.close()
    asyncio.run(exercise())


def test_shutdown_wakes_observers_and_cancels_owned_tasks():
    async def exercise():
        host = FleetSessionHost()
        host.create_session("closing", seed=1000, routes=load_routes_from_json())
        _, queue = host.subscribe("closing")
        queue.get_nowait()
        task = host._sessions["closing"].task
        await host.close()
        final = queue.get_nowait()
        assert final.error_code == "SESSION_CLOSED"
        assert task.done()
        await host.close()
        with pytest.raises(RuntimeError, match="closed"):
            host.create_session("after-close", seed=1000, routes=load_routes_from_json())
    asyncio.run(exercise())
