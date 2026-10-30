"""Build and cache a routable graph from the committed static map data.

The SQLite scene database owns the node/way topology used by renderers.  Its
current schema predates routing metadata, so the sibling committed ``map.osm``
file is used to enrich those same ways with direction, access, and speed tags.
No network service is contacted while building or using this graph.
"""

from __future__ import annotations

from collections import defaultdict
from dataclasses import dataclass
from functools import lru_cache
import math
from pathlib import Path
import sqlite3
from threading import RLock
from types import MappingProxyType
from typing import Literal, Mapping
import xml.etree.ElementTree as ET

import networkx as nx

from src.core.config import get_settings


RoutingMode = Literal["directed", "undirected_prototype"]

_METRES_PER_DEGREE_LATITUDE = 110_574.0
_METRES_PER_DEGREE_LONGITUDE_AT_EQUATOR = 111_320.0
_ACCESS_KEYS = ("access", "vehicle", "motor_vehicle", "motorcar")
_RESTRICTED_ACCESS = {
    "no",
    "permit",
    "customers",
    "delivery",
    "destination",
}
# The configured simulation represents an authorized site vehicle.  OSM
# ``private`` therefore describes an access-controlled road that it may use,
# rather than a physically undrivable road.  Keep the tag on RoadEdge for
# provenance, but do not remove the segment from the routing graph.
_DRIVABLE_HIGHWAY_TYPES = {
    "motorway",
    "motorway_link",
    "trunk",
    "trunk_link",
    "primary",
    "primary_link",
    "secondary",
    "secondary_link",
    "tertiary",
    "tertiary_link",
    "unclassified",
    "residential",
    "living_street",
    "service",
    "road",
}
_DEFAULT_SPEED_KPH = {
    "motorway": 90.0,
    "motorway_link": 50.0,
    "trunk": 70.0,
    "trunk_link": 45.0,
    "primary": 50.0,
    "primary_link": 40.0,
    "secondary": 45.0,
    "secondary_link": 35.0,
    "tertiary": 40.0,
    "tertiary_link": 30.0,
    "unclassified": 30.0,
    "residential": 30.0,
    "living_street": 15.0,
    "service": 20.0,
    "road": 25.0,
}


class RoadGraphError(RuntimeError):
    """Raised when committed scene data cannot produce a routable graph."""


@dataclass(frozen=True, slots=True)
class RoadEdge:
    """A directed road segment and the routing metadata retained on it."""

    source: int
    target: int
    length_m: float
    oneway: bool
    highway_type: str
    access_restrictions: Mapping[str, str]
    speed_estimate_kph: float | None
    road_id: int

    def graph_attributes(self) -> dict[str, object]:
        return {
            "source": self.source,
            "target": self.target,
            "length_m": self.length_m,
            "oneway": self.oneway,
            "highway_type": self.highway_type,
            "access_restrictions": dict(self.access_restrictions),
            "speed_estimate_kph": self.speed_estimate_kph,
            "road_id": self.road_id,
        }


@dataclass(frozen=True, slots=True)
class RoadGraphSnapshot:
    graph: nx.MultiDiGraph
    routing_mode: RoutingMode
    warnings: tuple[str, ...]


@dataclass(frozen=True, slots=True)
class _RoadRecord:
    road_id: int
    highway_type: str
    node_ids: tuple[int, ...]
    database_tags: Mapping[str, str]


class RoadGraphRepository:
    """Lazily build one graph per committed-map revision and cache it in memory."""

    def __init__(self, database_path: Path) -> None:
        self.database_path = Path(database_path).resolve()
        self.osm_path = self.database_path.with_name("map.osm")
        self._lock = RLock()
        self._cache_key: tuple[object, ...] | None = None
        self._snapshot: RoadGraphSnapshot | None = None

    def get(self) -> RoadGraphSnapshot:
        cache_key = self._source_cache_key()
        with self._lock:
            if self._snapshot is None or self._cache_key != cache_key:
                self._snapshot = self._build()
                self._cache_key = cache_key
            return self._snapshot

    def _source_cache_key(self) -> tuple[object, ...]:
        if not self.database_path.is_file():
            raise RoadGraphError(
                f"Static-scene database was not found at {self.database_path}"
            )
        database_stat = self.database_path.stat()
        osm_signature: tuple[int, int] | None = None
        if self.osm_path.is_file():
            osm_stat = self.osm_path.stat()
            osm_signature = (osm_stat.st_size, osm_stat.st_mtime_ns)
        return (database_stat.st_size, database_stat.st_mtime_ns, osm_signature)

    def _build(self) -> RoadGraphSnapshot:
        nodes, roads = self._read_topology()
        if not roads:
            raise RoadGraphError("Static-scene database contains no roads")

        osm_tags = _read_osm_way_tags(self.osm_path, {road.road_id for road in roads})
        has_complete_osm_metadata = bool(osm_tags) and all(
            road.road_id in osm_tags for road in roads
        )
        database_has_direction = all("oneway" in road.database_tags for road in roads)
        routing_mode: RoutingMode = (
            "directed"
            if has_complete_osm_metadata or database_has_direction
            else "undirected_prototype"
        )
        warnings: list[str] = []
        if routing_mode == "undirected_prototype":
            warnings.append(
                "Prototype routing: direction metadata is unavailable, so roads are "
                "treated as bidirectional and the route may go against one-way traffic."
            )

        graph = nx.MultiDiGraph()
        for node_id, node in nodes.items():
            graph.add_node(node_id, **node)

        restricted_road_count = 0
        for road in roads:
            if road.highway_type not in _DRIVABLE_HIGHWAY_TYPES:
                continue
            tags = dict(road.database_tags)
            tags.update(osm_tags.get(road.road_id, {}))
            access_restrictions = {
                key: tags[key] for key in _ACCESS_KEYS if tags.get(key)
            }
            if _blocks_general_motor_traffic(access_restrictions):
                restricted_road_count += 1
                continue

            direction = _oneway_direction(tags, road.highway_type, routing_mode)
            speed = _speed_estimate_kph(tags.get("maxspeed"), road.highway_type)
            for segment_index, (source, target) in enumerate(
                zip(road.node_ids, road.node_ids[1:])
            ):
                if source == target or source not in nodes or target not in nodes:
                    continue
                length = math.hypot(
                    float(nodes[target]["x"]) - float(nodes[source]["x"]),
                    float(nodes[target]["y"]) - float(nodes[source]["y"]),
                )
                if length <= 0.0:
                    continue
                if direction >= 0:
                    _add_edge(
                        graph,
                        RoadEdge(
                            source=source,
                            target=target,
                            length_m=length,
                            oneway=direction == 1,
                            highway_type=road.highway_type,
                            access_restrictions=MappingProxyType(access_restrictions),
                            speed_estimate_kph=speed,
                            road_id=road.road_id,
                        ),
                        segment_index,
                        "forward",
                    )
                if direction <= 0:
                    _add_edge(
                        graph,
                        RoadEdge(
                            source=target,
                            target=source,
                            length_m=length,
                            oneway=direction == -1,
                            highway_type=road.highway_type,
                            access_restrictions=MappingProxyType(access_restrictions),
                            speed_estimate_kph=speed,
                            road_id=road.road_id,
                        ),
                        segment_index,
                        "reverse",
                    )

        graph.remove_nodes_from(list(nx.isolates(graph)))
        if graph.number_of_nodes() == 0 or graph.number_of_edges() == 0:
            raise RoadGraphError("Static-scene data contains no routable road segments")
        graph.graph.update(
            routing_mode=routing_mode,
            source_database=str(self.database_path),
            source_osm=str(self.osm_path) if self.osm_path.is_file() else None,
            restricted_road_count=restricted_road_count,
        )
        return RoadGraphSnapshot(graph, routing_mode, tuple(warnings))

    def _read_topology(
        self,
    ) -> tuple[dict[int, dict[str, float]], tuple[_RoadRecord, ...]]:
        try:
            connection = sqlite3.connect(
                f"file:{self.database_path.as_posix()}?mode=ro", uri=True
            )
            connection.row_factory = sqlite3.Row
        except sqlite3.Error as error:
            raise RoadGraphError(f"Could not open static-scene database: {error}") from error

        try:
            bounds = connection.execute(
                "SELECT MIN(lon), MAX(lon), MIN(lat), MAX(lat) FROM nodes"
            ).fetchone()
            if bounds is None or any(value is None for value in bounds):
                raise RoadGraphError("Static-scene database contains no map nodes")
            origin_lon = (float(bounds[0]) + float(bounds[1])) * 0.5
            origin_lat = (float(bounds[2]) + float(bounds[3])) * 0.5
            longitude_scale = _METRES_PER_DEGREE_LONGITUDE_AT_EQUATOR * math.cos(
                math.radians(origin_lat)
            )
            nodes = {
                int(row["node_id"]): {
                    "x": (float(row["lon"]) - origin_lon) * longitude_scale,
                    "y": (float(row["lat"]) - origin_lat)
                    * _METRES_PER_DEGREE_LATITUDE,
                    "z": 0.0,
                    "latitude": float(row["lat"]),
                    "longitude": float(row["lon"]),
                }
                for row in connection.execute("SELECT node_id, lat, lon FROM nodes")
            }
            road_columns = {
                str(row[1]) for row in connection.execute("PRAGMA table_info(roads)")
            }
            optional_columns = [
                column
                for column in (*_ACCESS_KEYS, "oneway", "maxspeed")
                if column in road_columns
            ]
            selected_optional = "".join(f", r.{column}" for column in optional_columns)
            rows = connection.execute(
                f"""
                SELECT r.road_id, r.highway_type{selected_optional},
                       rn.node_id, rn.sequence
                FROM roads r
                JOIN road_nodes rn ON rn.road_id = r.road_id
                ORDER BY r.road_id, rn.sequence
                """
            )
            grouped_nodes: dict[int, list[int]] = defaultdict(list)
            road_metadata: dict[int, tuple[str, dict[str, str]]] = {}
            for row in rows:
                road_id = int(row["road_id"])
                node_id = int(row["node_id"])
                if not grouped_nodes[road_id] or grouped_nodes[road_id][-1] != node_id:
                    grouped_nodes[road_id].append(node_id)
                road_metadata[road_id] = (
                    str(row["highway_type"]),
                    {
                        column: str(row[column])
                        for column in optional_columns
                        if row[column] is not None and str(row[column]).strip()
                    },
                )
        except sqlite3.Error as error:
            raise RoadGraphError(f"Could not read static-scene topology: {error}") from error
        finally:
            connection.close()

        roads = tuple(
            _RoadRecord(
                road_id=road_id,
                highway_type=road_metadata[road_id][0],
                node_ids=tuple(node_ids),
                database_tags=MappingProxyType(road_metadata[road_id][1]),
            )
            for road_id, node_ids in grouped_nodes.items()
            if len(node_ids) >= 2
        )
        return nodes, roads


def _read_osm_way_tags(path: Path, road_ids: set[int]) -> dict[int, dict[str, str]]:
    if not path.is_file():
        return {}
    tags_by_way: dict[int, dict[str, str]] = {}
    try:
        for _event, element in ET.iterparse(path, events=("end",)):
            if element.tag != "way":
                continue
            raw_id = element.get("id")
            if raw_id is not None and int(raw_id) in road_ids:
                tags_by_way[int(raw_id)] = {
                    str(tag.get("k")): str(tag.get("v"))
                    for tag in element.findall("tag")
                    if tag.get("k") is not None and tag.get("v") is not None
                }
            element.clear()
    except (ET.ParseError, OSError, ValueError) as error:
        raise RoadGraphError(f"Could not read committed OSM metadata at {path}: {error}") from error
    return tags_by_way


def _blocks_general_motor_traffic(restrictions: Mapping[str, str]) -> bool:
    effective_access = next(
        (
            restrictions[key].strip().lower()
            for key in reversed(_ACCESS_KEYS)
            if key in restrictions
        ),
        None,
    )
    return effective_access in _RESTRICTED_ACCESS


def _oneway_direction(
    tags: Mapping[str, str], highway_type: str, routing_mode: RoutingMode
) -> int:
    """Return 1 for forward, -1 for reverse, and 0 for both directions."""

    if routing_mode == "undirected_prototype" and "oneway" not in tags:
        return 0
    value = tags.get("oneway", "").strip().lower()
    if value in {"-1", "reverse"}:
        return -1
    if value in {"yes", "true", "1"}:
        return 1
    if value in {"no", "false", "0"}:
        return 0
    if tags.get("junction", "").strip().lower() == "roundabout":
        return 1
    if highway_type in {"motorway", "motorway_link"}:
        return 1
    return 0


def _speed_estimate_kph(raw_maxspeed: str | None, highway_type: str) -> float | None:
    if raw_maxspeed:
        first_value = raw_maxspeed.split(";")[0].strip().lower()
        number = "".join(
            character for character in first_value if character.isdigit() or character == "."
        )
        if number:
            speed = float(number)
            if "mph" in first_value:
                speed *= 1.609344
            elif "knot" in first_value:
                speed *= 1.852
            if speed > 0.0:
                return speed
    return _DEFAULT_SPEED_KPH.get(highway_type)


def _add_edge(
    graph: nx.MultiDiGraph,
    edge: RoadEdge,
    segment_index: int,
    direction: str,
) -> None:
    graph.add_edge(
        edge.source,
        edge.target,
        key=(edge.road_id, segment_index, direction),
        **edge.graph_attributes(),
    )


@lru_cache
def get_road_graph_repository() -> RoadGraphRepository:
    return RoadGraphRepository(get_settings().map_database_path)


__all__ = [
    "RoadEdge",
    "RoadGraphError",
    "RoadGraphRepository",
    "RoadGraphSnapshot",
    "get_road_graph_repository",
]
