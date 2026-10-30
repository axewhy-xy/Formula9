"""
OSMnx-based graph loader for driving networks.

Author: Choo Jian Wei
Date Created: 12/09/2026
Last Modified: 12/09/2026

Description:
    This module provides functionality to load and save a graph for a specified area 
    using the OSMnx library. It includes functions to load the graph, get its bounds, 
    save the graph to a file, and save a summary of the graph's properties to a JSON file.
    The module can be executed as a script to build a demo graph for a specified area, 
    saving both the graph and its summary to specified output paths.

Usage:
    To use this module as a script, run the following command in the terminal:
        python OSM_graph_loader.py --help

    default:
        outpath: data/graphs/graph.graphml
        summary_out_path: data/graphs/graph_summary.json

"""

from __future__ import annotations

import argparse
import json
import logging
import math
import os
import tempfile
from collections.abc import Callable, Sequence
from pathlib import Path
from typing import Any, TypeAlias

import networkx as nx
import osmnx as ox
import requests


LOGGER = logging.getLogger(__name__)

OUTPUT_GRAPH_PATH = "data/graphs/graph.graphml"
OUTPUT_SUMMARY_PATH = "data/graphs/graph_summary.json"
OUTPUT_CACHE_PATH = "data/graphs/osmnx_cache"

# Optional environment overrides for OSMnx's Overpass network settings.
# The public overpass-api.de hostname round-robins across several physical
# servers, and depending on the network/route it's called from, some of
# those IPs can be unreachable while others work fine. Rather than hardcode
# a single "known good" mirror (which is specific to whichever network we
# tested from), these let each deployment/developer point at whatever
# endpoint works for them without touching code.
ENV_OVERPASS_URL = "OSMNX_OVERPASS_URL"
ENV_REQUESTS_TIMEOUT = "OSMNX_REQUESTS_TIMEOUT"

# Public Overpass instances are shared, rate-limited community infrastructure
# with no uptime guarantee -- even a mirror that's reachable right now can
# time out a few minutes later under load. load_graph() tries these in order
# and falls through to the next one on a network-level failure, so a single
# mirror having a bad moment doesn't fail the whole call. Order is: the
# generic hostname first (freshest data, load-balanced across servers), then
# two specific official servers behind that same hostname, then a
# third-party mirror as a last resort.
DEFAULT_OVERPASS_URLS: tuple[str, ...] = (
    "https://overpass.kumi.systems/api",
    "https://overpass-api.de/api",
    "https://lz4.overpass-api.de/api",
    "https://z.overpass-api.de/api",
)

PathLike: TypeAlias = str | os.PathLike[str]

"""
# -----------------------------------------------------------------------------------
# Function: _non_empty
# -----------------------------------------------------------------------------------
Description:
    This function checks if a given string value is non-empty and raises a ValueError 
    if it is empty or not a string.

Args:
    value (str): The string value to check.
    field_name (str): The name of the field being checked, used in the error message.

Returns:
    str: The stripped string value if it is non-empty.

Raises:
    ValueError: If the value is empty or not a string.

"""
def _non_empty(value: str, field_name: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"{field_name} must be a non-empty string")
    return value.strip()

"""
# -----------------------------------------------------------------------------------
# Function: _non_empty_cli_value
# -----------------------------------------------------------------------------------
Description:
    This function is a wrapper around the _non_empty function, specifically for use 
    with command-line argument parsing. It raises an argparse.ArgumentTypeError if the
    value is empty or not a string.

Args:
    value (str): The string value to check.

Returns:
    str: The stripped string value if it is non-empty.

Raises:
    argparse.ArgumentTypeError: If the value is empty or not a string.

"""
def _non_empty_cli_value(value: str) -> str:
    try:
        return _non_empty(value, "value")
    except ValueError as error:
        raise argparse.ArgumentTypeError(str(error)) from error

"""
# -----------------------------------------------------------------------------------
# Function: parse_args
# -----------------------------------------------------------------------------------
Description:
    This function parses command-line arguments for the script. It accepts both 
    hyphenated and legacy underscore option names. The parsed arguments include the 
    place name, area name, output paths for the graph and summary, and the cache path.

Args:
    argv (Sequence[str] | None): The list of command-line arguments to parse. If
                                 None, the arguments are taken from sys.argv.

Returns:
    argparse.Namespace: An object containing the parsed command-line arguments.

"""
def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    """Parse command-line arguments.

    Both hyphenated and legacy underscore option names are accepted.
    """
    parser = argparse.ArgumentParser(
        description="Download and save an OSM driving graph for a place."
    )
    parser.add_argument(
        "--place-name",
        "--place_name",
        dest="place_name",
        required=True,
        type=_non_empty_cli_value,
        help="Place query understood by OpenStreetMap's geocoder.",
    )
    parser.add_argument(
        "--area-name",
        "--area_name",
        dest="area_name",
        required=True,
        type=_non_empty_cli_value,
        help="Human-readable area name stored in the summary.",
    )
    parser.add_argument(
        "--out-path",
        "--out_path",
        dest="out_path",
        default=OUTPUT_GRAPH_PATH,
        help="Destination GraphML path (default: %(default)s).",
    )
    parser.add_argument(
        "--summary-out-path",
        "--summary_out_path",
        dest="summary_out_path",
        default=OUTPUT_SUMMARY_PATH,
        help="Destination summary JSON path (default: %(default)s).",
    )
    parser.add_argument(
        "--cache-path",
        "--cache_path",
        dest="cache_path",
        default=OUTPUT_CACHE_PATH,
        help="OSMnx HTTP cache directory (default: %(default)s).",
    )
    return parser.parse_args(argv)

"""
# -----------------------------------------------------------------------------------
# Function: configure_cache
# -----------------------------------------------------------------------------------
Description:
    This function creates and configures the OSMnx HTTP cache directory. It ensures that
    the specified cache path exists and sets the OSMnx settings to use this cache.

Args:
    cache_path (PathLike): The path to the cache directory. Defaults to OUTPUT_CACHE_PATH.

Returns:
    Path: The path to the configured cache directory.

"""
def configure_cache(cache_path: PathLike = OUTPUT_CACHE_PATH) -> Path:
    """Create and configure the OSMnx HTTP cache directory."""
    path = _prepare_directory(cache_path, "cache_path")
    ox.settings.use_cache = True
    ox.settings.cache_folder = str(path)
    return path

"""
# -----------------------------------------------------------------------------------
# Function: configure_network_settings
# -----------------------------------------------------------------------------------
Description:
    This function applies optional environment-variable overrides for OSMnx's
    Overpass endpoint and HTTP request timeout. It is a no-op for any setting
    whose environment variable is not set, so OSMnx's own defaults are left
    untouched unless a deployment/developer explicitly opts in.

    This exists because the public overpass-api.de hostname resolves to
    several physical servers, and some of those IPs can be unreachable from
    a given network/route while others work fine -- hardcoding a single
    "known good" mirror in code would only be correct for whichever network
    it was tested from.

Args:
    None. Reads OSMNX_OVERPASS_URL and OSMNX_REQUESTS_TIMEOUT from the
    environment.

Returns:
    None

Raises:
    ValueError: If OSMNX_REQUESTS_TIMEOUT is set but is not a valid integer.

"""
def configure_network_settings() -> None:
    """Apply optional OSMNX_REQUESTS_TIMEOUT env override; log Overpass candidates."""
    LOGGER.info("Overpass endpoints to try, in order: %s", _overpass_url_candidates())

    timeout_value = os.environ.get(ENV_REQUESTS_TIMEOUT)
    if timeout_value:
        try:
            timeout_seconds = int(timeout_value)
        except ValueError as error:
            raise ValueError(
                f"{ENV_REQUESTS_TIMEOUT} must be an integer number of seconds, "
                f"got {timeout_value!r}"
            ) from error
        LOGGER.info(
            "Using OSMnx request timeout from %s: %ds", ENV_REQUESTS_TIMEOUT, timeout_seconds
        )
        ox.settings.requests_timeout = timeout_seconds

"""
# -----------------------------------------------------------------------------------
# Function: _overpass_url_candidates
# -----------------------------------------------------------------------------------
Description:
    This function returns the ordered list of Overpass endpoint URLs that
    load_graph should try. If OSMNX_OVERPASS_URL is set, it is parsed as a
    comma-separated list and used verbatim, in order (so a caller can pin a
    single known-good endpoint, or supply their own fallback order).
    Otherwise the built-in DEFAULT_OVERPASS_URLS list is used.

Args:
    None. Reads OSMNX_OVERPASS_URL from the environment.

Returns:
    list[str]: The ordered list of Overpass endpoint URLs to try.

Raises:
    ValueError: If OSMNX_OVERPASS_URL is set but contains no usable URL
        (e.g. it is empty, or only commas/whitespace).

"""
def _overpass_url_candidates() -> list[str]:
    """Return the ordered list of Overpass endpoints load_graph should try."""
    override = os.environ.get(ENV_OVERPASS_URL)
    if not override:
        return list(DEFAULT_OVERPASS_URLS)

    candidates = [url.strip() for url in override.split(",") if url.strip()]
    if not candidates:
        raise ValueError(f"{ENV_OVERPASS_URL} was set but contained no usable URL")
    return candidates

"""
# -----------------------------------------------------------------------------------
# Function: load_graph
# -----------------------------------------------------------------------------------
Description:
    This function downloads a simplified driving graph for a specified place using the 
    OSMnx library. It raises a ValueError if no road-network nodes are found for the 
    given place.

Args:
    place_name (str): The name of the place to load the graph for.

Returns:
    nx.MultiDiGraph: The downloaded driving graph.

"""
def load_graph(place_name: str) -> nx.MultiDiGraph:
    """Download a simplified driving graph for ``place_name``.

    Tries each Overpass endpoint from _overpass_url_candidates() in order,
    falling through to the next on a network-level failure. A non-network
    error (e.g. the place can't be geocoded) is not retried across mirrors.

    Raises:
        ValueError: If place_name is empty, no road-network nodes are
            found, or the place cannot be resolved.
        ConnectionError: If every candidate Overpass endpoint fails at the
            network level.
    """
    place_name = _non_empty(place_name, "place_name")

    graph: nx.MultiDiGraph | None = None
    last_error: Exception | None = None
    for overpass_url in _overpass_url_candidates():
        ox.settings.overpass_url = overpass_url
        try:
            graph = ox.graph_from_place(place_name, network_type="drive", simplify=True)
            break
        except requests.exceptions.RequestException as error:
            LOGGER.warning(
                "Overpass endpoint %s failed for %r (%s); trying next endpoint",
                overpass_url,
                place_name,
                error,
            )
            last_error = error
            continue

    if graph is None:
        raise ConnectionError(
            f"All Overpass endpoints failed while loading {place_name!r}"
        ) from last_error

    if graph.number_of_nodes() == 0:
        raise ValueError(f"No road-network nodes were found for {place_name!r}")
    return graph

"""
# -----------------------------------------------------------------------------------
# Function: get_bounds
# -----------------------------------------------------------------------------------
Description:
    This function calculates the north, south, east, and west bounds of a graph based on 
    its node coordinates. It raises a ValueError if the graph is empty or if any node 
    lacks finite x/y coordinates.

Args:
    graph (nx.Graph): The graph for which to calculate the bounds.

Returns:
    dict[str, float]: A dictionary containing the north, south, east, and west bounds.

"""
def get_bounds(graph: nx.Graph) -> dict[str, float]:
    """Return north/south/east/west bounds from graph node coordinates.

    Raises:
        ValueError: If the graph is empty or a node lacks finite ``x``/``y``
            coordinates.
    """
    north = -math.inf
    south = math.inf
    east = -math.inf
    west = math.inf
    node_count = 0

    for node, data in graph.nodes(data=True):
        node_count += 1
        latitude = _node_coordinate(node, data, "y")
        longitude = _node_coordinate(node, data, "x")
        north = max(north, latitude)
        south = min(south, latitude)
        east = max(east, longitude)
        west = min(west, longitude)

    if node_count == 0:
        raise ValueError("Cannot calculate bounds for an empty graph")

    return {"north": north, "south": south, "east": east, "west": west}

"""
# -----------------------------------------------------------------------------------
# Function: _node_coordinate
# -----------------------------------------------------------------------------------
Description:
    This function retrieves a specific coordinate (latitude or longitude) from a graph 
    node's data. It raises a ValueError if the coordinate is missing, invalid, or not 
    finite.

Args:
    node (Any): The node identifier.
    data (dict[str, Any]): The node's data dictionary.
    key (str): The key for the coordinate to retrieve ("x" for longitude, "y" for latitude).

Returns:
    float: The retrieved coordinate value.

"""
def _node_coordinate(node: Any, data: dict[str, Any], key: str) -> float:
    try:
        coordinate = float(data[key])
    except (KeyError, TypeError, ValueError) as error:
        raise ValueError(
            f"Graph node {node!r} has an invalid or missing {key!r} coordinate"
        ) from error

    if not math.isfinite(coordinate):
        raise ValueError(
            f"Graph node {node!r} has a non-finite {key!r} coordinate"
        )
    return coordinate

"""
# -----------------------------------------------------------------------------------
# Function: create_summary
# -----------------------------------------------------------------------------------
Description:
    This function creates a summary of the graph's properties, including the area name, 
    node count, edge count, and bounds. It raises a ValueError if the area name is empty.

Args:
    graph (nx.Graph): The graph for which to create the summary.
    area_name (str): A descriptive name for the area.

Returns:
    dict[str, Any]: A dictionary containing the summary of the graph's properties.

"""
def create_summary(graph: nx.Graph, area_name: str) -> dict[str, Any]:
    """Build the serializable metadata stored alongside a graph."""
    area_name = _non_empty(area_name, "area_name")
    return {
        "area_name": area_name,
        "node_count": graph.number_of_nodes(),
        "edge_count": graph.number_of_edges(),
        "bounds": get_bounds(graph),
    }

"""
# -----------------------------------------------------------------------------------
# Function: save_graph
# -----------------------------------------------------------------------------------
Description:
    This function saves a graph to a specified output path in GraphML format. It raises 
    a ValueError if the graph is empty or if the output path points to a directory.

Args:
    graph (nx.Graph): The graph to save.
    out_path (PathLike): The output path for the saved graph file.

Returns:
    None

"""
def save_graph(graph: nx.Graph, out_path: PathLike) -> None:
    """Atomically save ``graph`` as GraphML, creating parent directories."""
    if graph.number_of_nodes() == 0:
        raise ValueError("Cannot save an empty graph")

    target = _prepare_output_path(out_path, "out_path")

    def write_graph(path: Path) -> None:
        ox.save_graphml(graph, filepath=path)

    _atomic_write(target, write_graph)

"""
# -----------------------------------------------------------------------------------
# Function: save_summary
# -----------------------------------------------------------------------------------
Description:
    This function saves a summary of the graph's properties to a specified output path in 
    JSON format. It raises a ValueError if the area name is empty or if the output path 
    points to a directory.

Args:
    graph (nx.Graph): The graph for which to save the summary.
    area_name (str): A descriptive name for the area.
    out_path (PathLike): The output path for the saved summary JSON file.

Returns:
    None

"""
def save_summary(graph: nx.Graph, area_name: str, out_path: PathLike) -> None:
    """Atomically save a UTF-8 JSON summary, creating parent directories."""
    summary = create_summary(graph, area_name)
    target = _prepare_output_path(out_path, "summary_out_path")

    def write_summary(path: Path) -> None:
        with path.open("w", encoding="utf-8", newline="\n") as json_file:
            json.dump(
                summary,
                json_file,
                ensure_ascii=False,
                allow_nan=False,
                indent=2,
            )
            json_file.write("\n")

    _atomic_write(target, write_summary)

"""
# -----------------------------------------------------------------------------------
# Function: build_demo_graph
# -----------------------------------------------------------------------------------
Description:
    This function downloads a graph for a specified place, saves the graph and its 
    summary to specified output paths, and returns the graph. It raises a ValueError if 
    the place name or area name is empty, or if the graph and summary output paths are 
    the same.

Args:
    place_name (str): The name of the place to load the graph for.
    area_name (str): A descriptive name for the area.
    out_path (PathLike): The output path for the saved graph file.
    summary_out_path (PathLike): The output path for the saved summary JSON file.

Returns:
    nx.MultiDiGraph: The downloaded driving graph.

"""
def build_demo_graph(
    place_name: str,
    area_name: str,
    out_path: PathLike = OUTPUT_GRAPH_PATH,
    summary_out_path: PathLike = OUTPUT_SUMMARY_PATH,
) -> nx.MultiDiGraph:
    """Download a graph, save it and its summary, then return the graph."""
    place_name = _non_empty(place_name, "place_name")
    area_name = _non_empty(area_name, "area_name")
    graph_target = _prepare_output_path(out_path, "out_path")
    summary_target = _prepare_output_path(summary_out_path, "summary_out_path")
    if graph_target.resolve() == summary_target.resolve():
        raise ValueError("Graph and summary output paths must be different")

    # Validate all graph-derived summary data before modifying either output.
    graph = load_graph(place_name)
    create_summary(graph, area_name)
    save_graph(graph, graph_target)
    save_summary(graph, area_name, summary_target)
    return graph

"""
# -----------------------------------------------------------------------------------
# Function: _prepare_directory
# -----------------------------------------------------------------------------------
Description:
    This function prepares a directory for use. It checks if the specified path exists 
    and is a directory, creating it if necessary. It raises a NotADirectoryError if the 
    path exists but is not a directory.

Args:
    path_value (PathLike): The path to the directory to prepare.
    field_name (str): The name of the field being checked, used in the error message.

Returns:
    Path: The prepared directory path.

"""
def _prepare_directory(path_value: PathLike, field_name: str) -> Path:
    path = _to_path(path_value, field_name)
    if path.exists() and not path.is_dir():
        raise NotADirectoryError(f"{field_name} is not a directory: {path}")
    path.mkdir(parents=True, exist_ok=True)
    return path

"""
# -----------------------------------------------------------------------------------
# Function: _prepare_output_path
# -----------------------------------------------------------------------------------
Description:
    This function prepares an output path for use. It checks if the specified path 
    includes a filename and is not a directory, creating parent directories if 
    necessary. It raises a ValueError if the path does not include a filename or an 
    IsADirectoryError if the path points to a directory.

Args:
    path_value (PathLike): The path to prepare.
    field_name (str): The name of the field being checked, used in the error message.

Returns:
    Path: The prepared output path.

"""
def _prepare_output_path(path_value: PathLike, field_name: str) -> Path:
    path = _to_path(path_value, field_name)
    if not path.name:
        raise ValueError(f"{field_name} must include a filename")
    if path.exists() and path.is_dir():
        raise IsADirectoryError(f"{field_name} points to a directory: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    return path

"""
# -----------------------------------------------------------------------------------
# Function: _to_path
# -----------------------------------------------------------------------------------
Description:
    This function converts a given path-like value to a Path object. It raises a 
    ValueError if the value is not a valid filesystem path.

Args:
    path_value (PathLike): The path-like value to convert.
    field_name (str): The name of the field being checked, used in the error message.

Returns:
    Path: The converted Path object.

"""
def _to_path(path_value: PathLike, field_name: str) -> Path:
    try:
        raw_path = os.fspath(path_value)
        if not isinstance(raw_path, str) or not raw_path.strip():
            raise ValueError
        return Path(raw_path).expanduser()
    except (TypeError, ValueError, OSError) as error:
        raise ValueError(f"{field_name} must be a valid filesystem path") from error

"""
# -----------------------------------------------------------------------------------
# Function: _atomic_write
# -----------------------------------------------------------------------------------
Description:
    This function writes data to a temporary file and then atomically replaces the target 
    file with the temporary file. It ensures that the write operation is atomic, meaning 
    that the target file is either fully written or not modified at all.

Args:
    target (Path): The target file path to write to.
    writer (Callable[[Path], None]): A callable that takes a Path and writes data to it.

Returns:
    None

"""
def _atomic_write(target: Path, writer: Callable[[Path], None]) -> None:
    """Write to a sibling temporary file and atomically replace ``target``."""
    file_descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{target.stem}.",
        suffix=target.suffix,
        dir=target.parent,
    )
    os.close(file_descriptor)
    temporary_path = Path(temporary_name)

    try:
        writer(temporary_path)
        os.replace(temporary_path, target)
    finally:
        temporary_path.unlink(missing_ok=True)

"""
# -----------------------------------------------------------------------------------
# Function: main
# -----------------------------------------------------------------------------------
Description:
    This function serves as the entry point for the command-line graph builder. It 
    parses command-line arguments, configures the OSMnx cache, downloads the driving 
    graph for the specified place, saves the graph and its summary, and logs the results. 
    It returns an exit code indicating success or failure.

Args:
    argv (Sequence[str] | None): The list of command-line arguments to parse. If
                                 None, the arguments are taken from sys.argv.
    
Returns:
    int: The exit code (0 for success, 1 for failure).

"""
def main(argv: Sequence[str] | None = None) -> int:
    """Run the command-line graph builder and return a process exit code."""
    args = parse_args(argv)
    try:
        configure_cache(args.cache_path)
        configure_network_settings() 
        LOGGER.info("Downloading driving graph for %s", args.place_name)
        graph = build_demo_graph(
            args.place_name,
            args.area_name,
            args.out_path,
            args.summary_out_path,
        )
    except Exception:
        LOGGER.exception("Failed to build graph for %s", args.place_name)
        return 1

    LOGGER.info(
        "Saved %d nodes and %d edges to %s",
        graph.number_of_nodes(),
        graph.number_of_edges(),
        args.out_path,
    )
    return 0

# main entry point
if __name__ == "__main__":
    logging.basicConfig(level=logging.INFO, format="%(levelname)s: %(message)s")
    raise SystemExit(main())

