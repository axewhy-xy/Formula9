"""Singapore bus stops, services and routes from LTA DataMall, kept in one SQLite file.

The file is written by ``python -m src.transit.import_lta`` and only read by the API.
"""

from __future__ import annotations

import sqlite3
from pathlib import Path

ATTRIBUTION = ("Contains information from LTA DataMall which is made available under the terms "
               "of the Singapore Open Data Licence version 1.0")

SCHEMA = """
CREATE TABLE bus_stops (
    code TEXT PRIMARY KEY,
    road_name TEXT NOT NULL,
    description TEXT NOT NULL,
    lat REAL NOT NULL,
    lon REAL NOT NULL
);
CREATE INDEX bus_stops_position ON bus_stops (lat, lon);
CREATE TABLE bus_services (
    service_no TEXT NOT NULL,
    direction INTEGER NOT NULL,
    operator TEXT NOT NULL,
    category TEXT NOT NULL,
    origin_code TEXT NOT NULL,
    destination_code TEXT NOT NULL,
    loop_description TEXT NOT NULL,
    PRIMARY KEY (service_no, direction)
);
CREATE TABLE bus_routes (
    service_no TEXT NOT NULL,
    direction INTEGER NOT NULL,
    stop_sequence INTEGER NOT NULL,
    stop_code TEXT NOT NULL,
    distance_km REAL,
    PRIMARY KEY (service_no, direction, stop_sequence)
);
CREATE INDEX bus_routes_stop ON bus_routes (stop_code);
CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
"""


class TransitUnavailable(RuntimeError):
    """The transit database has not been imported yet, or cannot be read."""


def _text(row: dict, key: str) -> str:
    value = row.get(key)
    if value is None:
        return ""
    return str(value).strip()


def write_database(path: Path, stops: list[dict], services: list[dict], routes: list[dict],
                   imported_at: str, attribution: str = ATTRIBUTION) -> dict[str, int]:
    """Write a fresh database from raw DataMall rows, replacing any older file at once.

    Stops without usable coordinates are skipped; LTA lists a few placeholders at 0, 0.
    Returns how many rows of each kind were kept.
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    partial = path.with_name(path.name + ".partial")
    partial.unlink(missing_ok=True)
    connection = sqlite3.connect(partial)
    try:
        connection.executescript(SCHEMA)
        stop_rows = []
        for row in stops:
            code = _text(row, "BusStopCode")
            latitude = float(row.get("Latitude") or 0)
            longitude = float(row.get("Longitude") or 0)
            if not code or (latitude == 0 and longitude == 0):
                continue
            stop_rows.append((code, _text(row, "RoadName"), _text(row, "Description"), latitude, longitude))
        connection.executemany("INSERT OR REPLACE INTO bus_stops VALUES (?, ?, ?, ?, ?)", stop_rows)
        service_rows = [
            (_text(row, "ServiceNo"), int(row.get("Direction") or 1), _text(row, "Operator"),
             _text(row, "Category"), _text(row, "OriginCode"), _text(row, "DestinationCode"),
             _text(row, "LoopDesc"))
            for row in services if _text(row, "ServiceNo")
        ]
        connection.executemany("INSERT OR REPLACE INTO bus_services VALUES (?, ?, ?, ?, ?, ?, ?)", service_rows)
        route_rows = []
        for row in routes:
            if not _text(row, "ServiceNo") or not _text(row, "BusStopCode"):
                continue
            distance = row.get("Distance")
            if distance is not None:
                distance = float(distance)
            route_rows.append((_text(row, "ServiceNo"), int(row.get("Direction") or 1),
                               int(row.get("StopSequence") or 0), _text(row, "BusStopCode"), distance))
        connection.executemany("INSERT OR REPLACE INTO bus_routes VALUES (?, ?, ?, ?, ?)", route_rows)
        connection.executemany("INSERT INTO metadata VALUES (?, ?)",
                               [("imported_at", imported_at), ("attribution", attribution)])
        connection.commit()
    finally:
        connection.close()
    partial.replace(path)
    return {"stops": len(stop_rows), "services": len(service_rows), "routes": len(route_rows)}


class TransitStore:
    """Read-only queries over an imported transit database."""

    def __init__(self, path: Path) -> None:
        self.path = path

    def _connect(self) -> sqlite3.Connection:
        if not self.path.is_file():
            raise TransitUnavailable(
                f"No bus data at {self.path}; run 'python -m src.transit.import_lta' from backend/")
        try:
            connection = sqlite3.connect(f"file:{self.path.as_posix()}?mode=ro", uri=True)
        except sqlite3.Error as error:
            raise TransitUnavailable(f"Could not open bus data: {error}") from error
        connection.row_factory = sqlite3.Row
        return connection

    def _imported_at(self, connection: sqlite3.Connection) -> str | None:
        row = connection.execute("SELECT value FROM metadata WHERE key = 'imported_at'").fetchone()
        if row is None:
            return None
        return str(row["value"])

    def _attribution(self, connection: sqlite3.Connection) -> str:
        row = connection.execute("SELECT value FROM metadata WHERE key = 'attribution'").fetchone()
        return ATTRIBUTION if row is None else str(row["value"])

    def stops_in(self, south: float, west: float, north: float, east: float, limit: int) -> dict:
        """Stops inside the box, at most ``limit``; ``truncated`` says whether more were left out."""
        connection = self._connect()
        try:
            rows = connection.execute(
                "SELECT code, road_name, description, lat, lon FROM bus_stops"
                " WHERE lat BETWEEN ? AND ? AND lon BETWEEN ? AND ? ORDER BY code LIMIT ?",
                (south, north, west, east, limit + 1)).fetchall()
            imported_at = self._imported_at(connection)
            attribution = self._attribution(connection)
        except sqlite3.Error as error:
            raise TransitUnavailable(f"Could not read bus stops: {error}") from error
        finally:
            connection.close()
        stops = [{"code": row["code"], "road_name": row["road_name"], "description": row["description"],
                  "latitude": row["lat"], "longitude": row["lon"]} for row in rows[:limit]]
        return {"stops": stops, "truncated": len(rows) > limit, "imported_at": imported_at,
                "attribution": attribution}

    def services(self) -> dict:
        connection = self._connect()
        try:
            rows = connection.execute(
                "SELECT service_no, direction, operator, category, origin_code, destination_code,"
                " loop_description FROM bus_services ORDER BY service_no, direction").fetchall()
        except sqlite3.Error as error:
            raise TransitUnavailable(f"Could not read bus services: {error}") from error
        finally:
            connection.close()
        return {"services": [dict(row) for row in rows], "attribution": ATTRIBUTION}

    def route(self, service_no: str, direction: int) -> dict | None:
        """The stops a service calls at, in order, or None for an unknown service/direction."""
        connection = self._connect()
        try:
            rows = connection.execute(
                "SELECT r.stop_sequence, r.distance_km, s.code, s.road_name, s.description, s.lat, s.lon"
                " FROM bus_routes r JOIN bus_stops s ON s.code = r.stop_code"
                " WHERE r.service_no = ? AND r.direction = ? ORDER BY r.stop_sequence",
                (service_no, direction)).fetchall()
        except sqlite3.Error as error:
            raise TransitUnavailable(f"Could not read bus route: {error}") from error
        finally:
            connection.close()
        if not rows:
            return None
        stops = [{"sequence": row["stop_sequence"], "distance_km": row["distance_km"], "code": row["code"],
                  "road_name": row["road_name"], "description": row["description"],
                  "latitude": row["lat"], "longitude": row["lon"]} for row in rows]
        return {"service_no": service_no, "direction": direction, "stops": stops, "attribution": ATTRIBUTION}
