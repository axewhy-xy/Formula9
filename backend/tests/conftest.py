"""
Pytest fixtures shared across the backend test suite.

Author: Choo Jian Wei

Description:
    Configures OSMnx's Overpass endpoint and request timeout for integration
    tests that hit live OpenStreetMap data (see
    test_graph_loader_integration.py), so a slow/unreachable mirror fails
    fast instead of hanging for minutes per request.

    The actual values come from the OSMNX_OVERPASS_URL / OSMNX_REQUESTS_TIMEOUT
    environment variables (see graph_loader.configure_network_settings), with
    defaults below that were confirmed reachable from this machine's network
    via Test-NetConnection. The generic overpass-api.de hostname round-robins
    across several physical servers, and one of those IPs (162.55.144.139) is
    unreachable from this network while others -- like lz4.overpass-api.de,
    an official endpoint for the same main Overpass API instance -- work fine.

    If these tests start failing with ConnectTimeout again on a different
    network, re-run the Test-NetConnection checks against overpass-api.de,
    lz4.overpass-api.de, z.overpass-api.de, and overpass.kumi.systems to find
    a reachable endpoint, then either set OSMNX_OVERPASS_URL yourself or
    update the default here.
"""

import os

import pytest

from backend.src.routing import graph_loader

os.environ.setdefault(
    graph_loader.ENV_OVERPASS_URL,
    ",".join(
        [
        "https://overpass.kumi.systems/api",
        "https://overpass-api.de/api",
        "https://lz4.overpass-api.de/api",
        "https://z.overpass-api.de/api",

        ]
    ),
)
# OSMNX_REQUESTS_TIMEOUT: seconds to wait per Overpass endpoint before
# falling through to the next one. Raise this if a reachable mirror (e.g.
# kumi.systems) is timing out mid-response ("Read timed out") rather than
# failing to connect at all ("ConnectTimeoutError") -- it's just slow, not dead.
os.environ.setdefault(graph_loader.ENV_REQUESTS_TIMEOUT, "60")

graph_loader.configure_network_settings()


def pytest_configure(config):
    config.addinivalue_line("markers", "real_charge_rate: use the scenario's charge-to-full timing")


@pytest.fixture(autouse=True)
def fixed_charge_time(request):
    """The integrated scenario charges to full, which takes minutes of ticks. Tests of everything
    else use its short fixed charge, so they stay quick; test_charge_bookings opts into the real rate."""
    if request.node.get_closest_marker("real_charge_rate"):
        yield
        return
    from src.services.integrated_actor import get_workflow_definition
    data = get_workflow_definition().data
    saved = data.get("charge_time_scale")
    data["charge_time_scale"] = None
    yield
    data["charge_time_scale"] = saved
