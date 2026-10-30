"""
Integration tests for the graph_loader module — hits live OpenStreetMap data.

Author: Choo Jian Wei
Date: 13/09/2026
Last Modified: 13/09/2026

Description:
This module contains integration tests for the graph_loader module. Unlike
test_graph_loader.py, these tests do not mock OSMnx — they call load_graph
against real OpenStreetMap data for multiple Singapore areas to verify the
module actually works end to end, not just that its internal logic is correct
against fake/mocked graphs.

These tests are slow (network-dependent, can take 10-20+ minutes depending
on the reachable Overpass mirror's response time) and are skipped by default.
Run manually with:
    $env:RUN_INTEGRATION_TESTS="1"; python -m pytest backend/tests/test_graph_loader_integration.py -v

"""

import os
import unittest
import time
from backend.src.routing import graph_loader

RUN_INTEGRATION_TESTS = os.environ.get("RUN_INTEGRATION_TESTS") == "1"

"""
# ------------------------------------------------------------------------------------
# Class: GraphLoaderIntegrationTests
# ------------------------------------------------------------------------------------
Description:
This class contains integration tests for the graph_loader module, run against live
OpenStreetMap data rather than mocked/fake graphs. Skipped unless the
RUN_INTEGRATION_TESTS environment variable is set to "1", since these tests require
network access and are significantly slower than the mocked unit tests in
test_graph_loader.py.

Attributes:
    None

Members:
    test_load_multiple_graphs_from_osm: Tests that load_graph returns a sane, non-empty
        graph for several real bounded Singapore areas.
    test_edges_retain_metadata: Tests that every edge in a real loaded graph retains
        OSM metadata (length, road type) as required by the module's spec.
    test_invalid_place_raises_clean_error: Tests that a place name OSM's geocoder cannot
        resolve raises a clean ValueError rather than a raw OSMnx/geocoder exception.
    test_bounds_are_geo_distinct: Tests that two different real areas produce distinct
        bounds, confirming the loader isn't accidentally returning the same graph twice.

"""
def _load_graph_paced(place_name, pause=3):
        """Call graph_loader.load_graph with a small pause first, to avoid
        hammering the shared public Overpass mirror across successive calls."""
        time.sleep(pause)
        return graph_loader.load_graph(place_name)

@unittest.skipUnless(RUN_INTEGRATION_TESTS, "set Run_INTEGRATION_TESTS=1 to run")
class GraphLoaderIntegrationTests(unittest.TestCase):
    """
    # --------------------------------------------------------------------------------
    # Function: test_load_multiple_graphs_from_osm
    # --------------------------------------------------------------------------------
    Description:
    Loads a real drivable graph for several bounded Singapore areas (one-north, Punggol,
    Tanjong Pagar) and checks each is non-empty and within a sane node/edge count range,
    confirming load_graph works against live OSM data rather than only mocked graphs.

    Args:
        None

    Returns:
        None
    """
    def test_load_multiple_graphs_from_osm(self):
        places =["one-north, Singapore", "Punggol, Singapore", "Tanjong Pagar, Singapore"]
        for place in places:
            with self.subTest(place=place):
                graph = _load_graph_paced(place)
                self.assertIsNotNone(graph)
                self.assertGreater(len(graph.nodes), 0)
                self.assertLess(len(graph.edges), 20000)            
    """
    # --------------------------------------------------------------------------------
    # Function: test_edges_retain_metadata
    # --------------------------------------------------------------------------------
    Description:
    Loads a real graph for one-north and checks every edge retains the OSM metadata
    (highway/road type, length) that the module's spec requires be preserved.

    Args:
        None

    Returns:
        None

    """
    def test_edges_retain_metadata(self):
        place ="one-north, Singapore"
        graph = _load_graph_paced(place)
        for u, v, data in graph.edges(data=True):
            self.assertIn("highway", data)
            self.assertIn("length", data)
    """
    # --------------------------------------------------------------------------------
    # Function: test_invalid_place_raises_clean_error
    # --------------------------------------------------------------------------------
    Description:
    Calls load_graph with a place name OSM's geocoder cannot resolve, and confirms it
    raises a ValueError rather than letting a raw OSMnx/geocoder exception propagate.

    Args:
        None

    Returns:
        None

    """
    def test_invalid_place_raises_clean_error(self):
        with self.assertRaises(ValueError):
            _load_graph_paced("this-place-does-not-exist-12345")
    """
    # --------------------------------------------------------------------------------
    # Function: test_bounds_are_geo_distinct
    # --------------------------------------------------------------------------------
    Description:
    Loads two different real areas (one-north, Raffles Place) and confirms their
    computed bounds are different, verifying the loader returns area-specific data
    rather than accidentally returning the same graph for different place names.

    Args:
        None

    Returns:
        None
    """
    def test_bounds_are_geo_distinct(self):
        place1 = "one-north, Singapore"
        place2 = "Raffles Place, Singapore"
        graph1 = _load_graph_paced(place1)
        graph2 = _load_graph_paced(place2)
        bounds1= graph_loader.get_bounds(graph1)
        bounds2= graph_loader.get_bounds(graph2)
        self.assertNotEqual(bounds1, bounds2)
