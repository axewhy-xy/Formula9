"""World events: incidents as things that happen at a place on the road network, owned by no shuttle.

A world event names a type, a location (an operating-network edge, or a junction), when it starts, how long
it lasts, and its catalogue timings: how far ahead of a shuttle it is placed (warning_s), how long after it
starts the operator is told (detection_delay_s) and how long the road takes to recover (recovery_s).

A scenario is a timeline of scheduled events plus the random generator (SimulatedIncidentCatalog). This
module only describes and validates events; the session actor places them and the IncidentService is the
only writer of incidents. Every event is a plain dict in the actor's checkpoint, so a restored session
replays exactly what it had placed.
"""

from __future__ import annotations

import math
import re
from dataclasses import asdict, dataclass

# Every type a world event can have. With background traffic, the traffic plays each out: a breakdown pins
# a vehicle in its lane, a collision two and closes its scene, a signal fault sticks a junction's lights on
# red, and a road closure closes its road (src.traffic.city_traffic). Without it, each has the fallback its
# kind gives it (EVENT_KINDS): a speed factor on the roads around an accident, a held shuttle at a closure,
# and a shuttle held at the faulted junction's stop line.
EVENT_TYPES = ("BREAKDOWN", "COLLISION", "SIGNAL_FAULT", "ROAD_CLOSURE")
# The incident kind (the closure or congestion model the backend and the 3D client already know) each type
# is played out as. A breakdown blocks a lane, so it is drawn and slowed like a small accident.
EVENT_KINDS = {"BREAKDOWN": "ROAD_ACCIDENT", "COLLISION": "ROAD_ACCIDENT", "ROAD_CLOSURE": "ROAD_BLOCK",
               "SIGNAL_FAULT": "SIGNAL_FAULT"}
# The world type of each of the random generator's template kinds
KIND_EVENTS = {"ROAD_ACCIDENT": "COLLISION", "ROAD_BLOCK": "ROAD_CLOSURE"}
# Types a timeline may schedule. A signal fault is at a junction (a signalled junction's id); the rest are
# on an operating-network edge.
SCHEDULABLE = ("BREAKDOWN", "COLLISION", "ROAD_CLOSURE", "SIGNAL_FAULT")
AT_JUNCTION = ("SIGNAL_FAULT",)
_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.:-]{0,95}$")


@dataclass(frozen=True)
class WorldEvent:
    """One placed event, in session ticks. Exactly one of edge_id and node_id is set."""
    event_id: str
    event_type: str
    edge_id: str | None
    node_id: str | None
    start_tick: int
    duration_s: float
    warning_s: float = 0.0
    detection_delay_s: float = 0.0
    recovery_s: float = 0.0

    def __post_init__(self):
        if self.event_type not in EVENT_TYPES:
            raise ValueError(f"Unknown world event type {self.event_type!r}")
        if (self.edge_id is None) == (self.node_id is None):
            raise ValueError("A world event is at exactly one edge or junction")
        if type(self.start_tick) is not int or self.start_tick < 0:
            raise ValueError("A world event starts at a nonnegative tick")
        for name in ("duration_s", "warning_s", "detection_delay_s", "recovery_s"):
            value = getattr(self, name)
            if not isinstance(value, (int, float)) or isinstance(value, bool) or not math.isfinite(value) or value < 0:
                raise ValueError(f"{name} must be a finite nonnegative number")
        if self.duration_s <= 0:
            raise ValueError("A world event lasts a positive time")
        if self.detection_delay_s >= self.duration_s:
            raise ValueError("A world event must be detected before it ends")

    @property
    def kind(self) -> str:
        """The incident kind it is played out as: ROAD_BLOCK, ROAD_ACCIDENT or SIGNAL_FAULT."""
        return EVENT_KINDS[self.event_type]

    @property
    def location(self) -> dict:
        return {"edge_id": self.edge_id} if self.edge_id is not None else {"node_id": self.node_id}

    def detected_tick(self, step_s: float) -> int:
        """When the operator is told: detection_delay_s after it starts."""
        return self.start_tick + round(self.detection_delay_s / step_s)

    def end_tick(self, step_s: float) -> int:
        """When it is over: duration_s after it starts, and always after it is detected."""
        return max(self.detected_tick(step_s) + 1, self.start_tick + round(self.duration_s / step_s))

    def to_record(self) -> dict:
        return asdict(self)

    @classmethod
    def from_record(cls, record: dict) -> WorldEvent:
        return cls(**{key: record[key] for key in cls.__dataclass_fields__ if key in record})


def legacy_event(record: dict, step_s: float) -> dict:
    """The world event of an incident record from before world events (checkpoint v3): its kind's type on
    its incident edge, starting and detected at its trigger tick and lasting as long as it did."""
    kind = record.get("kind") or "ROAD_BLOCK"
    edge = (record.get("map_marker") or {}).get("edge_id") or next(iter(record.get("affected_edge_ids") or ()), None)
    if not edge:
        raise ValueError(f"Incident record {record.get('incident_id')!r} names no road")
    return WorldEvent(
        event_id=record["incident_id"], event_type=KIND_EVENTS.get(kind, "ROAD_CLOSURE"),
        edge_id=edge, node_id=None, start_tick=record.get("trigger_tick", 0),
        duration_s=max(1, record.get("clear_after_ticks", 1)) * step_s,
    ).to_record()


@dataclass(frozen=True)
class ScheduledEvent:
    """One entry of a scenario's timeline, in scenario seconds. Timings the entry leaves out come from the
    catalogue template of its kind. A signal fault is at a junction (node_id), anything else on an edge."""
    event_id: str
    event_type: str
    edge_id: str | None
    at_s: float
    duration_s: float
    warning_s: float | None = None
    detection_delay_s: float | None = None
    recovery_s: float | None = None
    node_id: str | None = None

    def place(self, step_s: float, template=None) -> WorldEvent:
        """The world event this entry places in a session."""
        def timing(name):
            value = getattr(self, name)
            return value if value is not None else (getattr(template, name) if template is not None else 0.0)
        return WorldEvent(event_id=self.event_id, event_type=self.event_type, edge_id=self.edge_id, node_id=self.node_id,
                          start_tick=round(self.at_s / step_s), duration_s=self.duration_s,
                          warning_s=timing("warning_s"), detection_delay_s=timing("detection_delay_s"),
                          recovery_s=timing("recovery_s"))

    def start_tick(self, step_s: float) -> int:
        return round(self.at_s / step_s)


TIMELINE_FIELDS = {"event_id", "event_type", "location", "at_s", "duration_s", "warning_s",
                   "detection_delay_s", "recovery_s"}


def load_timeline(rows, edges=None, junctions=None) -> list[ScheduledEvent]:
    """A scenario's timeline from its JSON rows, sorted by start time (then id).

    Each row is {"event_id", "event_type", "location": {"edge_id"}, "at_s", "duration_s"} with optional
    warning_s, detection_delay_s and recovery_s; a SIGNAL_FAULT's location is {"node_id"}, a signalled
    junction's id. With edges (the operating network's edge ids) or junctions (the signalled junctions'
    ids), every such location must be one of them. Raises ValueError on anything else, so a bad scenario
    fails at load rather than in a running session.
    """
    if rows is None:
        return []
    if not isinstance(rows, list):
        raise ValueError("A timeline is a list of scheduled events")
    found, seen = [], set()
    for row in rows:
        if not isinstance(row, dict):
            raise ValueError("A scheduled event is an object")
        unknown = set(row) - TIMELINE_FIELDS
        if unknown:
            raise ValueError(f"Scheduled event has unknown fields: {sorted(unknown)}")
        event_id = row.get("event_id")
        if not isinstance(event_id, str) or not _ID.match(event_id) or event_id in seen:
            raise ValueError(f"Scheduled event id {event_id!r} is missing, malformed or repeated")
        seen.add(event_id)
        event_type = row.get("event_type")
        if event_type not in SCHEDULABLE:
            raise ValueError(f"Scheduled event {event_id} has unsupported type {event_type!r}")
        location = row.get("location")
        place = "node_id" if event_type in AT_JUNCTION else "edge_id"
        if not isinstance(location, dict) or set(location) != {place} or not isinstance(location[place], str):
            raise ValueError(f"Scheduled event {event_id} must be located on one "
                             f"{'junction' if place == 'node_id' else 'edge'}")
        if place == "edge_id" and edges is not None and location["edge_id"] not in edges:
            raise ValueError(f"Scheduled event {event_id} is on unknown edge {location['edge_id']!r}")
        if place == "node_id" and junctions is not None and location["node_id"] not in junctions:
            raise ValueError(f"Scheduled event {event_id} is at unknown junction {location['node_id']!r}")
        timings = {}
        for name in ("at_s", "duration_s", "warning_s", "detection_delay_s", "recovery_s"):
            value = row.get(name)
            if value is None and name in ("warning_s", "detection_delay_s", "recovery_s"):
                continue
            if not isinstance(value, (int, float)) or isinstance(value, bool) or not math.isfinite(value) or value < 0:
                raise ValueError(f"Scheduled event {event_id} needs a finite nonnegative {name}")
            timings[name] = float(value)
        if timings["duration_s"] <= 0:
            raise ValueError(f"Scheduled event {event_id} must last a positive time")
        if timings.get("detection_delay_s", 0.0) >= timings["duration_s"]:
            raise ValueError(f"Scheduled event {event_id} must be detected before it ends")
        found.append(ScheduledEvent(event_id=event_id, event_type=event_type, edge_id=location.get("edge_id"),
                                    node_id=location.get("node_id"), **timings))
    return sorted(found, key=lambda event: (event.at_s, event.event_id))
