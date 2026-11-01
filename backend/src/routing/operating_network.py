"""The operating road network: directed edges with geometry, metadata and turn rules.

Reads an immutable fixture and answers questions about it. Nothing here touches a session; the
route editor service decides what a result means for a draft.
"""

from __future__ import annotations

import heapq
import math

from src.routing.build_operating_network import heading_change_deg

# Fit statuses a road can have for a vehicle
FITS, DOES_NOT_FIT, FIT_UNKNOWN = "PASS", "FAIL", "UNKNOWN"


def capitalised(text: str) -> str:
    return text[:1].upper() + text[1:]


def twin_id(edge_id: str) -> str:
    """The same road in the other direction, whether or not it exists."""
    name, index, direction = edge_id.rsplit(":", 2)
    return f"{name}:{index}:{'reverse' if direction == 'forward' else 'forward'}"


class OperatingNetwork:
    def __init__(self, fixture: dict):
        self.edges: dict[str, dict] = fixture["edges"]
        self.rules: dict = fixture["rules"]
        self.lengths = {key: sum(math.dist(a, b) for a, b in zip(edge["path"], edge["path"][1:]))
                        for key, edge in self.edges.items()}
        self.outgoing: dict[int, list[str]] = {}
        for key in sorted(self.edges):
            self.outgoing.setdefault(self.edges[key]["source"], []).append(key)

    def road_name(self, edge_id: str) -> str:
        edge = self.edges.get(edge_id)
        if edge is None:
            return "an unknown road"
        return edge["road"] or f"an unnamed {edge['class'].replace('_', ' ')} road"

    def fit(self, edge_id: str, config: dict) -> tuple[str, str]:
        """Whether the vehicle may use this road, and a sentence saying why."""
        edge, name = self.edges[edge_id], self.road_name(edge_id)
        if edge["access"] == "closed_to_buses":
            return DOES_NOT_FIT, f"{capitalised(name)} is closed to buses."
        if edge["access"] == "destination_only":
            return DOES_NOT_FIT, f"{capitalised(name)} is for local access only."
        if any(key not in config for key in ("vehicle_width_m", "vehicle_height_m", "minimum_side_clearance_m")):
            return FIT_UNKNOWN, f"The shuttle's size isn't on record, so it can't be cleared to use {name}."
        problems = []
        needed_width =float(config["vehicle_width_m"]) + 2 * float(config["minimum_side_clearance_m"])
        if edge["width_m"] is None:
            return FIT_UNKNOWN, f"No width is recorded for {name}, so the shuttle can't be cleared to use it."
        if edge["width_m"] < needed_width:
            problems.append(f"it is {edge['width_m']:.1f} m wide and the shuttle needs {needed_width:.1f} m")
        if edge["height_m"] is None:
            return FIT_UNKNOWN, f"No headroom is recorded for {name}, so the shuttle can't be cleared to pass under it."
        if edge["height_m"] < float(config["vehicle_height_m"]):
            problems.append(f"its height limit is {edge['height_m']:.1f} m and the shuttle is {float(config['vehicle_height_m']):.1f} m tall")
        if problems:
            return DOES_NOT_FIT, f"The shuttle doesn't fit on {name}: {' and '.join(problems)}."
        return FITS, f"The shuttle fits on {name}."

    def fit_status(self, edge_id: str, config: dict) -> str:
        return self.fit(edge_id, config)[0]

    def turn_allowed(self, first: str, second: str) -> bool:
        """A U-turn on the road, or one sharp enough to be one, is not something a shuttle does."""
        if twin_id(first) == second:
            return False
        change = heading_change_deg(self.edges[first]["path"], self.edges[second]["path"])
        return change <= float(self.rules["max_turn_deg"])

    def turn_description(self, first: str, second: str) -> str:
        return f"The shuttle can't U-turn from {self.road_name(first)} into {self.road_name(second)}."

    def plan(self, start_node: int, end_node: int, stops: list[list[str]], waypoints: list[str],
             closed: set[str], config: dict) -> tuple[list[str], str | None]:
        """The shortest loop that drives every waypoint road and serves the stops in order.

        Each stop is the choice of edges that arrive at its kerb. A waypoint is the clicked road in
        either direction, placed wherever in the loop lengthens it least. A waypoint on a road the
        shuttle can't use is still driven, so validation can point at it; between waypoints the
        plan keeps to roads it may use. Returns the edges, or the waypoint that could not be reached.
        """
        best, unreached = self._through(start_node, end_node, stops, waypoints, closed, config)
        return (best[1], unreached) if best else ([], unreached)

    def _through(self, start_node: int, end_node: int, stops: list[list[str]], waypoints: list[str],
                 closed: set[str], config: dict, incoming_edge: str | None = None):
        """The best plan as (length, edges), or None with the waypoint that could not be reached."""
        choices = [[key, *([twin_id(key)] if twin_id(key) in self.edges else [])] for key in waypoints]
        usable = {key for key in self.edges if key not in closed and self.fit_status(key, config) == FITS}
        usable.update(key for options in [*choices, *stops] for key in options)
        planner = _Planner(self, usable)
        # Getting to or away from a road the shuttle can't use may take more of them: a closed road
        # is closed all along. Allowed only for such a waypoint, so validation reports each one.
        anywhere = _Planner(self, set(self.edges))
        sequence = [list(options) for options in stops]
        best = planner.route(start_node, end_node, sequence, incoming_edge=incoming_edge)
        for waypoint, options in zip(waypoints, choices):
            placed = (self._place(planner, start_node, end_node, sequence, options, incoming_edge)
                      or self._place(anywhere, start_node, end_node, sequence, options, incoming_edge))
            if placed is None:
                return None, waypoint
            best, sequence = placed
            if best is not None and any(key not in usable for key in best[1]):
                planner = anywhere
        return best, None

    @staticmethod
    def _place(planner: "_Planner", start_node: int, end_node: int, sequence: list[list[str]], options: list[str],
               incoming_edge: str | None = None):
        placed = None
        for position in range(len(sequence) + 1):
            candidate = [*sequence[:position], options, *sequence[position:]]
            result = planner.route(start_node, end_node, candidate, incoming_edge=incoming_edge)
            if result is not None and (placed is None or result[0] < placed[0][0]):
                placed = (result, candidate)
        return placed

    def continuation(self, prefix: list[str], end_node: int, stops: list[list[str]], closed: set[str], config: dict,
                     waypoints: list[str] = ()) -> tuple[list[str], str | None]:
        """Keep the driven prefix and plan from its last road through any waypoints, respecting its next turn.

        Only a picked road may be closed or unfit, so validation can point at it. Returns the edges,
        or the waypoint that could not be reached.
        """
        if not prefix or any(edge not in self.edges or edge in closed for edge in prefix):
            return [], None
        open_stops = [[key for key in options if key not in closed and self.fit_status(key, config) == FITS]
                      for options in stops]
        best, unreached = self._through(self.edges[prefix[-1]]["target"], end_node, open_stops, list(waypoints),
                                        closed, config, incoming_edge=prefix[-1])
        return (prefix + best[1] if best else []), unreached


class _Planner:
    """One plan's searches. The same few roads start every leg, so each search runs once."""

    def __init__(self, network: OperatingNetwork, usable: set[str]):
        self.network, self.usable = network, usable
        self.searches: dict[object, tuple[dict[str, float], dict[str, str]]] = {}
        self.turns: dict[tuple[str, str], bool] = {}

    def _turn(self, first: str, second: str) -> bool:
        key = (first, second)
        if key not in self.turns:
            self.turns[key] = self.network.turn_allowed(first, second)
        return self.turns[key]

    def _search(self, key: object, sources: dict[str, float]) -> tuple[dict[str, float], dict[str, str]]:
        """Shortest driving distance from starting edges to every edge, turn rules included."""
        if key in self.searches:
            return self.searches[key]
        network = self.network
        best, previous = dict(sources), {}
        queue = [(cost, edge) for edge, cost in sorted(sources.items())]
        heapq.heapify(queue)
        while queue:
            cost, edge = heapq.heappop(queue)
            if cost > best[edge]:
                continue
            for ahead in network.outgoing.get(network.edges[edge]["target"], []):
                if ahead not in self.usable or not self._turn(edge, ahead):
                    continue
                reached = cost + network.lengths[ahead]
                if reached < best.get(ahead, math.inf):
                    best[ahead], previous[ahead] = reached, edge
                    heapq.heappush(queue, (reached, ahead))
        self.searches[key] = best, previous
        return best, previous

    def _from_node(self, node: int) -> tuple[dict[str, float], dict[str, str]]:
        sources = {edge: self.network.lengths[edge] for edge in self.network.outgoing.get(node, []) if edge in self.usable}
        return self._search(("node", node), sources)

    @staticmethod
    def _walk_back(previous: dict[str, str], edge: str, stop_at: str | None) -> list[str]:
        """The edges after stop_at, up to and including edge."""
        path = [edge]
        while path[-1] != stop_at and path[-1] in previous:
            path.append(previous[path[-1]])
        if path[-1] == stop_at:
            path.pop()
        path.reverse()
        return path

    def route(self, start_node: int, end_node: int, sequence: list[list[str]],
              *, incoming_edge: str | None = None) -> tuple[float, list[str]] | None:
        best, previous = (self._from_node(start_node) if incoming_edge is None else
                          self._search(incoming_edge, {incoming_edge: 0.0}))
        if not sequence:
            return self._finish([(0.0, incoming_edge, [], best, previous)], end_node)
        reached = {option: (best[option], self._walk_back(previous, option, incoming_edge))
                   for option in sequence[0] if option in best}
        for options in sequence[1:]:
            following = {}
            for option in options:
                choices = []
                for prior, (total, path) in sorted(reached.items()):
                    if option == prior:
                        choices.append((total, path))
                        continue
                    best, previous = self._search(prior, {prior: 0.0})
                    if option in best:
                        choices.append((total + best[option], path + self._walk_back(previous, option, prior)))
                if choices:
                    following[option] = min(choices, key=lambda item: item[0])
            reached = following
            if not reached:
                return None
        return self._finish([(total, prior, path, *self._search(prior, {prior: 0.0})) for prior, (total, path) in sorted(reached.items())], end_node)

    def _finish(self, legs: list, end_node: int) -> tuple[float, list[str]] | None:
        finished = []
        edges = self.network.edges
        for total, prior, path, best, previous in legs:
            if prior is not None and edges[prior]["target"] == end_node:
                finished.append((total, path))
                continue
            arrivals = [edge for edge in best if edges[edge]["target"] == end_node and edge != prior]
            if arrivals:
                arrival = min(arrivals, key=lambda edge: (best[edge], edge))
                finished.append((total + best[arrival], path + self._walk_back(previous, arrival, prior)))
        return min(finished, key=lambda item: item[0]) if finished else None
