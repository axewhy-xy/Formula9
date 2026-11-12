"""What a live incident closes to the background traffic: the scene on its road, and the queue backing up.

Ported from the Unity client's IncidentSimulation, which draws the same incident: a road block closes its
whole road, an accident the stretch either side of the wrecks, both ways; and the queue grows back along
the roads leading to it, at GROWTH_MPS up to REACH_M, one way. The background traffic keeps off all of
it (src.traffic.city_traffic.CityTraffic.set_hazards), so it stops behind the queue the client draws, and
a vehicle the queue reaches leaves the road: its place is one of the queue's own cars.

The roads are the session's operating network (the route editor's), by edge id, as the incident's map
marker names them.
"""

from __future__ import annotations

from src.traffic.city_traffic import line_at, line_length

BLOCK, ACCIDENT = "ROAD_BLOCK", "ROAD_ACCIDENT"
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

    def add_lane(self, points: list[tuple[float, float]], start_m: float) -> None:
        length = line_length(points)
        if length > 0.01:
            self.lanes.append((points, start_m, length))


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
        result.add_lane(between(points, 0.0, head)[::-1], 0.0)
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
            result.add_lane(between(path, max(0.0, feeder_length - (REACH_M - start_m)), feeder_length)[::-1], start_m)
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


def live_hazards(selections: list[dict], snapshot, edges: dict[str, dict], step_s: float, tick: int) -> list:
    """Every live incident's hazards at a tick, from the session's incident selections and its latest
    snapshot, as the client's CityIncidents works them out from the incidents' events.

    An incident is live from when it is detected. A road block lasts while its closure does (an
    informational one until its time runs out); an accident until it is settled or its time runs out.
    """
    if not selections:
        return []
    closed = {edge for d in snapshot.disruptions if d.active_from_tick <= tick
              and (d.active_until_tick is None or tick < d.active_until_tick) for edge in d.affected_edge_ids}
    found = []
    for selection in selections:
        marker = selection.get("map_marker")
        incident = next((i for i in snapshot.incidents if i.incident_id == selection.get("incident_id")), None)
        if not marker or incident is None:
            continue
        edge_id = marker.get("edge_id") or ""
        kind = selection.get("kind") or ""
        until = None if selection.get("requires_guidance") else incident.detected_tick + selection.get("clear_after_ticks", 0)
        closure = next((d for d in snapshot.disruptions
                        if edge_id in d.affected_edge_ids and d.active_from_tick >= incident.detected_tick), None)
        if kind == BLOCK:
            clears = closure.active_until_tick if closure is not None and closure.active_until_tick is not None else until
            live = tick < clears if clears is not None else closure is not None
        else:
            live = incident.state not in ("RESOLVED", "UNRESOLVED") and (until is None or tick < until)
        if not live:
            continue
        start, end = marker.get("start") or {}, marker.get("end") or {}
        ends = ((start.get("x_m", 0.0), start.get("y_m", 0.0)), (end.get("x_m", 0.0), end.get("y_m", 0.0)))
        found.extend(hazards(plan(kind, edge_id, edges, closed, ends), (tick - incident.detected_tick) * step_s))
    return found
