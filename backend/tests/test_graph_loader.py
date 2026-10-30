"""
Tests for the graph_loader module.

Author: Axewhy
Date: 12/09/2026
Last Modified: 12/09/2026

Description:
This module contains unit tests for the graph_loader module, which is responsible for downloading and 
saving OpenStreetMap (OSM) driving graphs for specified places. The tests cover various functionalities
of the graph_loader module, including argument parsing, graph loading, summary saving, and atomic file writing.

"""

import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import networkx as nx

from backend.src.routing import graph_loader

"""
# ------------------------------------------------------------------------------------
# Function: make_graph
# ------------------------------------------------------------------------------------
Description:
This function creates a simple directed graph using the NetworkX library. The graph 
consists of two nodes with specified coordinates and a single directed edge connecting 
them. It is used in the unit tests to provide a sample graph for testing various 
functionalities of the graph_loader module.

Args:
    None

Returns:
    nx.MultiDiGraph: A directed graph with two nodes and one edge.

"""
def make_graph() -> nx.MultiDiGraph:
    graph = nx.MultiDiGraph()
    graph.add_node(1, x=103.75, y=1.20)
    graph.add_node(2, x=103.90, y=1.35)
    graph.add_edge(1, 2)
    return graph

"""
# ------------------------------------------------------------------------------------
# Class: GraphLoaderTests
# ------------------------------------------------------------------------------------
Description:
This class contains unit tests for the graph_loader module. It inherits from 
unittest.TestCase and includes various test methods to verify the correctness of the 
graph_loader functionalities.The tests cover scenarios such as getting graph bounds, 
saving summaries, atomic file writing, argument parsing, and cache configuration.

Attributes:
    None

Members:
    test_get_bounds: Tests the get_bounds function for a valid graph.
    test_get_bounds_rejects_empty_graph: Tests that get_bounds raises an error for an empty graph.
    test_get_bounds_rejects_invalid_coordinates: Tests that get_bounds raises an error for invalid coordinates.
    test_save_summary_creates_parent_and_writes_utf8_json: Tests that save_summary creates parent directories and writes a UTF-8 JSON summary.
    test_save_graph_is_atomic_and_creates_parent: Tests that save_graph performs atomic writing and creates parent directories.
    test_failed_atomic_write_preserves_existing_file: Tests that a failed atomic write preserves the existing file.
    test_load_graph_trims_place_name_and_rejects_empty_result: Tests that load_graph trims the place name and raises an error for an empty result.
    test_parse_args_requires_names_and_supports_legacy_flags: Tests that parse_args requires place and area names and supports legacy flags.
    test_configure_cache_accepts_current_directory: Tests that configure_cache accepts the current directory and sets the cache folder correctly.
    test_build_rejects_shared_output_path_before_download: Tests that build_demo_graph raises an error if the output paths for the graph and summary are the same before downloading.
    
"""
class GraphLoaderTests(unittest.TestCase):
    def test_get_bounds(self) -> None:
        self.assertEqual(
            graph_loader.get_bounds(make_graph()),
            {"north": 1.35, "south": 1.20, "east": 103.90, "west": 103.75},
        )

    def test_get_bounds_rejects_empty_graph(self) -> None:
        with self.assertRaisesRegex(ValueError, "empty graph"):
            graph_loader.get_bounds(nx.MultiDiGraph())

    def test_get_bounds_rejects_invalid_coordinates(self) -> None:
        graph = nx.MultiDiGraph()
        graph.add_node("bad", x=float("nan"), y=1.0)

        with self.assertRaisesRegex(ValueError, "non-finite 'x'"):
            graph_loader.get_bounds(graph)

    def test_save_summary_creates_parent_and_writes_utf8_json(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            output = Path(temporary_directory) / "nested" / "summary.json"
            graph_loader.save_summary(make_graph(), "Tanjong Pagar – demo", output)

            summary = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual(summary["area_name"], "Tanjong Pagar – demo")
            self.assertEqual(summary["node_count"], 2)
            self.assertEqual(summary["edge_count"], 1)

    def test_save_graph_is_atomic_and_creates_parent(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            output = Path(temporary_directory) / "nested" / "graph.graphml"

            def fake_save_graphml(_graph, filepath) -> None:
                Path(filepath).write_text("complete graph", encoding="utf-8")

            with patch.object(
                graph_loader.ox, "save_graphml", side_effect=fake_save_graphml
            ):
                graph_loader.save_graph(make_graph(), output)

            self.assertEqual(output.read_text(encoding="utf-8"), "complete graph")
            self.assertEqual(list(output.parent.glob(".graph.*.graphml")), [])

    def test_failed_atomic_write_preserves_existing_file(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            output = Path(temporary_directory) / "graph.graphml"
            output.write_text("previous graph", encoding="utf-8")

            def failed_save(_graph, filepath) -> None:
                Path(filepath).write_text("partial graph", encoding="utf-8")
                raise OSError("disk failure")

            with patch.object(graph_loader.ox, "save_graphml", side_effect=failed_save):
                with self.assertRaisesRegex(OSError, "disk failure"):
                    graph_loader.save_graph(make_graph(), output)

            self.assertEqual(output.read_text(encoding="utf-8"), "previous graph")

    def test_load_graph_trims_place_name_and_rejects_empty_result(self) -> None:
        with patch.object(
            graph_loader.ox, "graph_from_place", return_value=nx.MultiDiGraph()
        ) as graph_from_place:
            with self.assertRaisesRegex(ValueError, "No road-network nodes"):
                graph_loader.load_graph("  one-north, Singapore  ")

        graph_from_place.assert_called_once_with(
            "one-north, Singapore", network_type="drive", simplify=True
        )

    def test_parse_args_requires_names_and_supports_legacy_flags(self) -> None:
        args = graph_loader.parse_args(
            ["--place_name", "Singapore", "--area_name", "Demo"]
        )
        self.assertEqual(args.place_name, "Singapore")
        self.assertEqual(args.area_name, "Demo")

    def test_configure_cache_accepts_current_directory(self) -> None:
        previous_folder = graph_loader.ox.settings.cache_folder
        try:
            self.assertEqual(graph_loader.configure_cache("."), Path("."))
            self.assertEqual(graph_loader.ox.settings.cache_folder, ".")
            self.assertTrue(graph_loader.ox.settings.use_cache)
        finally:
            graph_loader.ox.settings.cache_folder = previous_folder

    def test_build_rejects_shared_output_path_before_download(self) -> None:
        with patch.object(graph_loader, "load_graph") as load_graph:
            with self.assertRaisesRegex(ValueError, "must be different"):
                graph_loader.build_demo_graph("Singapore", "Demo", "same", "same")

        load_graph.assert_not_called()


if __name__ == "__main__":
    unittest.main()
