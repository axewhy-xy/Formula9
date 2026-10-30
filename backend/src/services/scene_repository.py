"""Backend-owned SQLite access and canonical static-scene conversion."""

from __future__ import annotations

from collections import defaultdict
from functools import lru_cache
from math import cos, isfinite, pi
from pathlib import Path
import sqlite3

from src.core.config import get_settings
from src.schemas.scene import (
    BuildingFeature,
    PoiFeature,
    RoadFeature,
    SceneBounds,
    SceneChunkResponse,
    SceneCounts,
    SceneMetadataResponse,
    ScenePoint,
    SCENE_TRANSFORM_REVISION,
)
from src.schemas.simulation import CoordinatesMetadata, GeographicOrigin


SCENE_ID = "default"
_METRES_PER_DEGREE_LATITUDE = 110_574.0
_METRES_PER_DEGREE_LONGITUDE_AT_EQUATOR = 111_320.0


class SceneRepositoryError(RuntimeError):
    """Raised when the configured static-scene database cannot be consumed."""


class SceneNotFoundError(SceneRepositoryError):
    """Raised when a caller requests a scene other than the configured scene."""


class SceneRepository:
    """Own SQLite queries and expose only renderer-neutral local coordinates."""

    def __init__(self, database_path: Path) -> None:
        self.database_path = Path(database_path).resolve()

    def _connect(self) -> sqlite3.Connection:
        if not self.database_path.is_file():
            raise SceneRepositoryError(
                f"Static-scene database was not found at {self.database_path}"
            )
        connection = sqlite3.connect(
            f"file:{self.database_path.as_posix()}?mode=ro", uri=True
        )
        connection.row_factory = sqlite3.Row
        return connection

    def _ensure_scene(self, scene_id: str) -> None:
        if scene_id != SCENE_ID:
            raise SceneNotFoundError(f"Static scene '{scene_id}' was not found")

    def _revision(self) -> str:
        stat = self.database_path.stat()
        revision = f"sqlite-{stat.st_size:x}-{stat.st_mtime_ns:x}"
        # Route planning enriches this database's topology with tags from the
        # committed sibling OSM file. Include it in the public revision so a
        # client cannot preview a route against stale direction/access data.
        osm_path = self.database_path.with_name("map.osm")
        if osm_path.is_file():
            osm_stat = osm_path.stat()
            revision += f"-osm-{osm_stat.st_size:x}-{osm_stat.st_mtime_ns:x}"
        return revision

    @staticmethod
    def _coordinate_metadata(origin_latitude: float, origin_longitude: float) -> CoordinatesMetadata:
        return CoordinatesMetadata(
            origin=GeographicOrigin(
                latitude=origin_latitude,
                longitude=origin_longitude,
            )
        )

    @staticmethod
    def _project(
        latitude: float,
        longitude: float,
        origin_latitude: float,
        origin_longitude: float,
    ) -> ScenePoint:
        longitude_scale = _METRES_PER_DEGREE_LONGITUDE_AT_EQUATOR * cos(
            origin_latitude * pi / 180.0
        )
        return ScenePoint(
            x=(longitude - origin_longitude) * longitude_scale,
            y=(latitude - origin_latitude) * _METRES_PER_DEGREE_LATITUDE,
            z=0.0,
        )

    @staticmethod
    def _unproject_bounds(
        bounds: SceneBounds,
        origin_latitude: float,
        origin_longitude: float,
    ) -> tuple[float, float, float, float]:
        longitude_scale = _METRES_PER_DEGREE_LONGITUDE_AT_EQUATOR * cos(
            origin_latitude * pi / 180.0
        )
        return (
            origin_longitude + bounds.min_x / longitude_scale,
            origin_longitude + bounds.max_x / longitude_scale,
            origin_latitude + bounds.min_y / _METRES_PER_DEGREE_LATITUDE,
            origin_latitude + bounds.max_y / _METRES_PER_DEGREE_LATITUDE,
        )

    def _source_metadata(
        self, connection: sqlite3.Connection
    ) -> tuple[float, float, float, float, float, float]:
        row = connection.execute(
            "SELECT MIN(lon), MAX(lon), MIN(lat), MAX(lat) FROM nodes"
        ).fetchone()
        if row is None or any(value is None for value in row):
            raise SceneRepositoryError("Static-scene database contains no map nodes")
        min_lon, max_lon, min_lat, max_lat = map(float, row)
        if not all(isfinite(value) for value in (min_lon, max_lon, min_lat, max_lat)):
            raise SceneRepositoryError("Static-scene database has non-finite bounds")
        return (
            min_lon,
            max_lon,
            min_lat,
            max_lat,
            (min_lat + max_lat) * 0.5,
            (min_lon + max_lon) * 0.5,
        )

    def metadata(self, scene_id: str = SCENE_ID) -> SceneMetadataResponse:
        self._ensure_scene(scene_id)
        with self._connect() as connection:
            min_lon, max_lon, min_lat, max_lat, origin_lat, origin_lon = (
                self._source_metadata(connection)
            )
            minimum = self._project(min_lat, min_lon, origin_lat, origin_lon)
            maximum = self._project(max_lat, max_lon, origin_lat, origin_lon)
            counts = SceneCounts(
                roads=int(connection.execute("SELECT COUNT(*) FROM roads").fetchone()[0]),
                buildings=int(
                    connection.execute("SELECT COUNT(*) FROM buildings").fetchone()[0]
                ),
                pois=int(connection.execute("SELECT COUNT(*) FROM pois").fetchone()[0]),
            )
        return SceneMetadataResponse(
            scene_id=scene_id,
            scene_revision=self._revision(),
            transform_revision=SCENE_TRANSFORM_REVISION,
            coordinates=self._coordinate_metadata(origin_lat, origin_lon),
            bounds=SceneBounds(
                min_x=minimum.x,
                min_y=minimum.y,
                max_x=maximum.x,
                max_y=maximum.y,
            ),
            counts=counts,
        )

    @staticmethod
    def _road_types_for_lod(lod: int) -> tuple[str, ...] | None:
        if lod >= 3:
            return None
        types = ["motorway", "trunk"]
        if lod >= 1:
            types.extend(("primary", "secondary"))
        if lod >= 2:
            types.extend(
                ("tertiary", "residential", "unclassified", "living_street")
            )
        return tuple(types)

    def chunk(
        self,
        scene_id: str,
        bounds: SceneBounds,
        *,
        lod: int,
        include_buildings: bool,
        include_pois: bool,
    ) -> SceneChunkResponse:
        self._ensure_scene(scene_id)
        if bounds.min_x >= bounds.max_x or bounds.min_y >= bounds.max_y:
            raise ValueError("Scene bounds minimums must be less than maximums")

        with self._connect() as connection:
            _, _, _, _, origin_lat, origin_lon = self._source_metadata(connection)
            min_lon, max_lon, min_lat, max_lat = self._unproject_bounds(
                bounds, origin_lat, origin_lon
            )
            roads = self._query_roads(
                connection,
                min_lon,
                max_lon,
                min_lat,
                max_lat,
                origin_lat,
                origin_lon,
                lod,
            )
            buildings = (
                self._query_buildings(
                    connection,
                    min_lon,
                    max_lon,
                    min_lat,
                    max_lat,
                    origin_lat,
                    origin_lon,
                )
                if include_buildings
                else []
            )
            pois = (
                self._query_pois(
                    connection,
                    min_lon,
                    max_lon,
                    min_lat,
                    max_lat,
                    origin_lat,
                    origin_lon,
                )
                if include_pois
                else []
            )

        return SceneChunkResponse(
            scene_id=scene_id,
            scene_revision=self._revision(),
            transform_revision=SCENE_TRANSFORM_REVISION,
            coordinates=self._coordinate_metadata(origin_lat, origin_lon),
            requested_bounds=bounds,
            lod=lod,
            roads=roads,
            buildings=buildings,
            pois=pois,
        )

    def _query_roads(
        self,
        connection: sqlite3.Connection,
        min_lon: float,
        max_lon: float,
        min_lat: float,
        max_lat: float,
        origin_lat: float,
        origin_lon: float,
        lod: int,
    ) -> list[RoadFeature]:
        road_types = self._road_types_for_lod(lod)
        type_clause = ""
        parameters: list[object] = [min_lon, max_lon, min_lat, max_lat]
        if road_types:
            placeholders = ",".join("?" for _ in road_types)
            type_clause = f" AND r.highway_type IN ({placeholders})"
            parameters.extend(road_types)
        rows = connection.execute(
            f"""
            WITH matching_roads AS (
                SELECT r.road_id
                FROM roads r
                JOIN road_nodes rn ON rn.road_id = r.road_id
                JOIN nodes n ON n.node_id = rn.node_id
                GROUP BY r.road_id
                HAVING MAX(n.lon) >= ? AND MIN(n.lon) <= ?
                   AND MAX(n.lat) >= ? AND MIN(n.lat) <= ?{type_clause}
            )
            SELECT r.road_id, r.highway_type, n.lat, n.lon
            FROM matching_roads mr
            JOIN roads r ON r.road_id = mr.road_id
            JOIN road_nodes rn ON rn.road_id = r.road_id
            JOIN nodes n ON n.node_id = rn.node_id
            ORDER BY r.road_id, rn.sequence
            """,
            parameters,
        )
        grouped: dict[tuple[int, str], list[ScenePoint]] = defaultdict(list)
        for row in rows:
            grouped[(int(row["road_id"]), str(row["highway_type"]))].append(
                self._project(
                    float(row["lat"]),
                    float(row["lon"]),
                    origin_lat,
                    origin_lon,
                )
            )
        return [
            RoadFeature(road_id=road_id, highway_type=road_type, points=points)
            for (road_id, road_type), points in grouped.items()
            if len(points) >= 2
        ]

    def _query_buildings(
        self,
        connection: sqlite3.Connection,
        min_lon: float,
        max_lon: float,
        min_lat: float,
        max_lat: float,
        origin_lat: float,
        origin_lon: float,
    ) -> list[BuildingFeature]:
        rows = connection.execute(
            """
            WITH matching_buildings AS (
                SELECT building_id
                FROM building_nodes
                GROUP BY building_id
                HAVING MAX(lon) >= ? AND MIN(lon) <= ?
                   AND MAX(lat) >= ? AND MIN(lat) <= ?
            )
            SELECT b.building_id, b.building_type, bn.lat, bn.lon
            FROM matching_buildings mb
            JOIN buildings b ON b.building_id = mb.building_id
            JOIN building_nodes bn ON bn.building_id = b.building_id
            ORDER BY b.building_id, bn.sequence
            """,
            (min_lon, max_lon, min_lat, max_lat),
        )
        grouped: dict[tuple[int, str], list[ScenePoint]] = defaultdict(list)
        for row in rows:
            grouped[(int(row["building_id"]), str(row["building_type"]))].append(
                self._project(
                    float(row["lat"]),
                    float(row["lon"]),
                    origin_lat,
                    origin_lon,
                )
            )
        return [
            BuildingFeature(
                building_id=building_id,
                building_type=building_type,
                points=points,
            )
            for (building_id, building_type), points in grouped.items()
            if len(points) >= 3
        ]

    def _query_pois(
        self,
        connection: sqlite3.Connection,
        min_lon: float,
        max_lon: float,
        min_lat: float,
        max_lat: float,
        origin_lat: float,
        origin_lon: float,
    ) -> list[PoiFeature]:
        rows = connection.execute(
            """
            SELECT poi_id, lat, lon, poi_type, poi_value, name
            FROM pois
            WHERE lon BETWEEN ? AND ? AND lat BETWEEN ? AND ?
            ORDER BY poi_id
            """,
            (min_lon, max_lon, min_lat, max_lat),
        )
        return [
            PoiFeature(
                poi_id=int(row["poi_id"]),
                poi_type=str(row["poi_type"]),
                poi_value=str(row["poi_value"]),
                name=None if row["name"] is None else str(row["name"]),
                position=self._project(
                    float(row["lat"]),
                    float(row["lon"]),
                    origin_lat,
                    origin_lon,
                ),
            )
            for row in rows
        ]


@lru_cache
def get_scene_repository() -> SceneRepository:
    return SceneRepository(get_settings().map_database_path)


__all__ = [
    "SCENE_ID",
    "SceneNotFoundError",
    "SceneRepository",
    "SceneRepositoryError",
    "get_scene_repository",
]
