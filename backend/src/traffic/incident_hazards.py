"""What a live world event closes to the background traffic, and what it holds up.

A road block closes its whole road and an accident the stretch either side of the wrecks, both ways: the
background traffic keeps off that scene (src.traffic.city_traffic.CityTraffic.set_hazards) from when the
event starts until it clears (event_window). Nothing more is scripted: the traffic queues behind the scene
(or behind a breakdown's pinned vehicle, or at a faulted junction's red lights) of its own accord.

The queue the Unity client draws for a session without the backend's traffic grows back along the roads
leading to the scene, at GROWTH_MPS up to REACH_M, one way (plan and hazards, ported from its
IncidentSimulation). That fully grown queue is still each event's footprint: the stretches of road a
shuttle meets it on, which the session's exposure check uses (footprint, signal_footprint).

The roads are the session's operating network (the route editor's), by edge id, as the incident's map
marker names them.
"""

from __future__ import annotations

from src.services.incident_scenarios.world_events import ONE_ROAD_EVENTS
from src.traffic import route_signals
from src.traffic.city_traffic import line_at, line_length

BLOCK, ACCIDENT, SIGNAL_FAULT = "ROAD_BLOCK", "ROAD_ACCIDENT", "SIGNAL_FAULT"
# A record that never triggers
NEVER = 10**12
# A signalled junction's approaches are looked for among the roads with a point this close to it
JUNCTION_REACH_M = 250.0
LEAVES_LINE_M = 1.0
REACH_M, GROWTH_MPS = 140.0, 5.0
# An accident's traffic stops this far short of the middle of its road
WRECK_CLEAR_M = 12.0
# The wrecks, van and cones stretch this far along the road either side of the site
SCENE_REACH_M = 14.0
MAX_LANES = 16


def between(points, start: float, end: float) -> list[tuple[float, float]]:
    """The part of a line between two distances along it."""
    x, y, _, _ = line_at(points, start)
    result = [(x, y)]
    walked = 0.0
    for a, b in zip(points, points[1:]):
        walked += ((b[0] - a[0]) ** 2 + (b[1] - a[1]) ** 2) ** 0.5
        if start < walked < end:
            result.append(tuple(b))
    x, y, _, _ = line_at(points, end)
    result.append((x, y))
    return result


class IncidentPlan:
    """An incident's road, and each road of the queue behind it: its line from the end nearest the
    incident outwards, and how far the queue has already come when it reaches that road."""

    def __init__(self, kind: str, road: list[tuple[float, float]]):
        self.kind = kind
        self.road = road
        self.lanes: list[tuple[list[tuple[float, float]], float, float]] = []
        # Where each lane lies on its road: (edge id, from m, to m) along the road the way it runs
        self.spans: list[tuple[str, float, float]] = []

    def add_lane(self, points: list[tuple[float, float]], start_m: float,
                 span: tuple[str, float, float] | None = None) -> None:
        length = line_length(points)
        if length > 0.01:
            self.lanes.append((points, start_m, length))
            if span is not None:
                self.spans.append(span)


def plan(kind: str, edge_id: str, edges: dict[str, dict], closed: set[str],
         marker: tuple[tuple[float, float], tuple[float, float]]) -> IncidentPlan:
    """The roads an incident on this operating-network edge holds up. Edges map an id to its source and
    target nodes and its path; a closed edge carries no traffic towards the incident."""
    edge = edges.get(edge_id)
    points = [tuple(p) for p in edge["path"]] if edge else [marker[0], marker[1]]
    result = IncidentPlan(kind, points)
    length = line_length(points)
    # A closure stops traffic where the road begins; an accident part way along it
    head = 0.0 if kind == BLOCK else max(0.0, length / 2 - WRECK_CLEAR_M)
    if head > 0:
        result.add_lane(between(points, 0.0, head)[::-1], 0.0, (edge_id, 0.0, head) if edge else None)
    if edge is None:
        return result
    # Breadth first up the roads whose traffic flows into this one, until the queue's reach runs out
    feeding: dict[int, list[str]] = {}
    for key in sorted(edges):
        feeding.setdefault(edges[key]["target"], []).append(key)
    seen = {edge_id}
    pending = [(edge_id, head)]
    while pending:
        road_id, start_m = pending.pop(0)
        road = edges[road_id]
        for feeder_id in feeding.get(road["source"], ()):
            feeder = edges[feeder_id]
            # Not the road's own other direction: that traffic is leaving
            if feeder["source"] == road["target"] or feeder_id in closed:
                continue
            if len(result.lanes) >= MAX_LANES or feeder_id in seen:
                continue
            seen.add(feeder_id)
            path = [tuple(p) for p in feeder["path"]]
            feeder_length = line_length(path)
            tail = max(0.0, feeder_length - (REACH_M - start_m))
            result.add_lane(between(path, tail, feeder_length)[::-1], start_m, (feeder_id, tail, feeder_length))
            if start_m + feeder_length < REACH_M:
                pending.append((feeder_id, start_m + feeder_length))
    return result


def hazards(incident: IncidentPlan, elapsed_s: float) -> list[tuple[list[tuple[float, float]], bool]]:
    """What the traffic must keep off this long after the incident: the closed road or the accident
    scene, both ways, and as much of the queue as has formed, the way it is waiting."""
    length = line_length(incident.road)
    scene = incident.road if incident.kind == BLOCK else between(
        incident.road, max(0.0, length / 2 - SCENE_REACH_M), min(length, length / 2 + SCENE_REACH_M))
    found = [(scene, True)]
    queue = min(max(elapsed_s * GROWTH_MPS, 0.0), REACH_M)
    for points, start_m, lane_length in incident.lanes:
        formed = min(max(queue - start_m, 0.0), lane_length)
        if formed > 0:
            found.append((between(points, 0.0, formed)[::-1], False))
    return found


def footprint(kind: str, edge_id: str, edges: dict[str, dict], closed: set[str]) -> tuple[tuple[str, float, float], ...]:
    """Every stretch of road an incident on this edge holds up once its queue has fully formed: the
    scene (a closed road whole; an accident's wrecks, both ways) and the queue's lanes, each as (edge id,
    from m, to m) along that road the way it runs. A shuttle whose route crosses one of them meets it."""
    edge = edges.get(edge_id)
    if edge is None:
        return ()
    points = [tuple(p) for p in edge["path"]]
    length = line_length(points)
    found = list(plan(kind, edge_id, edges, closed, (points[0], points[-1])).spans)
    if kind == BLOCK:
        found.append((edge_id, 0.0, length))
    else:
        found.append((edge_id, max(0.0, length / 2 - SCENE_REACH_M), min(length, length / 2 + SCENE_REACH_M)))
        # The wrecks reach across the road: its other direction passes the same scene
        for other_id, other in edges.items():
            if other["source"] == edge["target"] and other["target"] == edge["source"] and other_id != edge_id:
                other_length = line_length([tuple(p) for p in other["path"]])
                found.append((other_id, max(0.0, other_length / 2 - SCENE_REACH_M),
                              min(other_length, other_length / 2 + SCENE_REACH_M)))
    return tuple(found)


def event_type(record: dict) -> str:
    """The world event type of an incident record; one from before world events has its kind's."""
    event = record.get("event") or {}
    return event.get("event_type") or ("ROAD_CLOSURE" if record.get("kind") == BLOCK else "COLLISION")


def event_window(record: dict, disruptions, incidents=()) -> tuple[int, int]:
    """The ticks a world event is on the road: from when it starts, which may be before it is detected,
    until it clears. A closure clears when its disruption ends, which an escalation may have brought
    forward; anything else when its record says, or sooner when an escalation brought its incident's
    clears_at_tick forward (incidents, the snapshot's)."""
    trigger = record.get("trigger_tick", NEVER)
    start = min((record.get("event") or {}).get("start_tick", trigger), trigger)
    until = trigger + record.get("clear_after_ticks", 0)
    closure = next((d for d in disruptions if d.disruption_id == record.get("disruption_id")), None)
    if closure is not None and closure.active_until_tick is not None:
        until = closure.active_until_tick
    incident = next((i for i in incidents if i.incident_id == record.get("incident_id")), None)
    if incident is not None and incident.clears_at_tick is not None:
        until = min(until, incident.clears_at_tick)
    return start, until


def scene(kind: str, road: list[tuple[float, float]]) -> list[tuple[float, float]]:
    """The stretch of an incident's road its scene covers: a closed road whole, an accident's wrecks."""
    if kind == BLOCK:
        return road
    length = line_length(road)
    return between(road, max(0.0, length / 2 - SCENE_REACH_M), min(length, length / 2 + SCENE_REACH_M))


def live_hazards(records: list[dict], snapshot, edges: dict[str, dict], tick: int) -> list:
    """Every live world event's scene at a tick, both ways, from the session's incident records and its
    latest snapshot: what the background traffic keeps off. A breakdown has no scene, only its pinned
    vehicle, a pedestrian on the road only its walker, and a signal fault only its red lights; no event has a drawn queue, since the traffic forms
    its own behind the scene."""
    found = []
    for record in records:
        start, until = event_window(record, snapshot.disruptions, getattr(snapshot, "incidents", ()))
        kind = record.get("kind")
        if not start <= tick < until or kind not in (BLOCK, ACCIDENT) or event_type(record) in ONE_ROAD_EVENTS:
            continue
        marker = record.get("map_marker") or {}
        edge = edges.get(marker.get("edge_id") or "")
        if edge is not None:
            road = [tuple(p) for p in edge["path"]]
        else:
            ends = marker.get("start") or {}, marker.get("end") or {}
            road = [(end.get("x_m", 0.0), end.get("y_m", 0.0)) for end in ends]
        found.append((scene(kind, road), True))
    return found


def signal_footprint(junction: dict, edges: dict[str, dict]) -> tuple[tuple[str, float, float], ...]:
    """Every stretch of road a signal fault at this junction holds up: each operating-network edge that
    crosses one of its stop lines, going its way, from REACH_M short of the line up to it, as (edge id,
    from m, to m) along that road the way it runs."""
    found = []
    x, y = junction["x_m"], junction["y_m"]
    for edge_id in sorted(edges):
        path = [tuple(p) for p in edges[edge_id]["path"]]
        if not any((px - x) ** 2 + (py - y) ** 2 <= JUNCTION_REACH_M ** 2 for px, py in path):
            continue
        for line in route_signals.stop_lines(path, [junction]):
            # A line at the very start of a road is behind it: that road leaves the junction
            if line.progress_m >= LEAVES_LINE_M:
                found.append((edge_id, max(0.0, line.progress_m - REACH_M), line.progress_m))
    return tuple(found)
