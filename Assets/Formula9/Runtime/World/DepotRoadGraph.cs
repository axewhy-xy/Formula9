/*
 * Shortest driving distance from anywhere on the road network to the nearest depot.
 *
 * Description:
 *     Built from the same CityRoad list the traffic renderer uses, so it follows the same one-way
 *     rules. One Dijkstra run on the REVERSED graph, started at the depots, gives every junction its
 *     distance to the nearest depot. A shuttle query is then a nearest-road lookup plus one
 *     dictionary read, cheap enough to run for every shuttle on every snapshot. The search is only
 *     redone when the depots or the closed roads change.
 */

using System.Collections.Generic;
using System.Linq;
using Formula9.Backend;
using UnityEngine;

namespace Formula9.World
{
    // -----------------------------------------------------------------------------------
    // Class: DepotRoadGraph
    // -----------------------------------------------------------------------------------
    // Description:
    //     Road graph with a precomputed distance-to-depot for every junction.
    //     Coordinates are the city's local metres: x east, y north (the backend's x_m, y_m).
    public sealed class DepotRoadGraph
    {
        //the values here are estimated defaults, can be tuned if needs to.

        // Farther than this from every road, a shuttle is not on the network (a depot yard, say).
        public const float SnapRadiusM = 40f;
        // Inside this of a depot the straight line is the trip.
        public const float AtDepotRadiusM = 40f;
        // Extra metres charged for the opposite direction of travel on a two-way road.
        private const float WrongWayPenaltyM = 60f;
        // Roads this much farther from the shuttle than the nearest one are not candidates.
        private const float SnapTieM = 2f;
        // Up to this many junctions within this distance of a depot count as its entrances; the
        // nearest junction always does, however far.
        private const int DepotEntrances = 3;
        private const float EntranceRadiusM = 150f;

        public struct Result
        {
            public bool Reachable;      // false when closures cut the shuttle off from every depot
            public bool OffNetwork;     // true when the distance is a straight line, not a road path
            public float DistanceM;
        }

        private sealed class Edge
        {
            public string Key, RoadId;
            public long From, To;
            public Vector2[] Points;
            public float[] Along;
            public float Length;
        }

        private struct Candidate { public Edge Edge; public float Lateral, Remaining, Score; }

        private readonly List<Edge> edges;                       // every directed road piece
        private readonly Dictionary<long, List<Edge>> incoming;  // junction -> the edges that END there
        private readonly Dictionary<long, Vector2> nodes;        // junction -> its position
        private readonly List<Vector2> depots;                   // depot positions
        private readonly HashSet<string> closed;                 // closed road IDs
        private Dictionary<long, float> toDepot;                 // junction -> metres to the nearest depot

        // -----------------------------------------------------------------------------------
        // Function: DepotRoadGraph (constructor)
        // -----------------------------------------------------------------------------------
        // Args:
        //     roads (IEnumerable<CityRoad>): The city's roads; two-way roads become two edges.
        public DepotRoadGraph(IEnumerable<CityRoad> roads)
        {
            foreach (var road in roads ?? Enumerable.Empty<CityRoad>())
            {
                if (road?.Points == null || road.Points.Count < 2) continue;
                var forward = road.Points.Select(p => new Vector2((float)p.X, (float)p.Y)).ToArray();
                Add(road, 1, forward, road.FromNode, road.ToNode);
                if (!road.Oneway) Add(road, -1, forward.Reverse().ToArray(), road.ToNode, road.FromNode);
            }
        }

        private void Add(CityRoad road, int direction, Vector2[] points, long from, long to)
        {
            var along = new float[points.Length];
            for (var i = 1; i < points.Length; i++) along[i] = along[i - 1] + Vector2.Distance(points[i - 1], points[i]);
            var length = along[along.Length - 1];
            if (length < 0.1f) return;
            // Same key the traffic renderer uses, so a closure naming "road|direction" matches.
            var edge = new Edge { Key = $"{road.Id}|{direction}", RoadId = road.Id, From = from, To = to, Points = points, Along = along, Length = length };
            edges.Add(edge);
            nodes[from] = points[0];
            nodes[to] = points[points.Length - 1];
            if (!incoming.TryGetValue(to, out var list)) incoming[to] = list = new List<Edge>();
            list.Add(edge);
        }

        // -----------------------------------------------------------------------------------
        // Function: SetDepots
        // -----------------------------------------------------------------------------------
        // Description:
        //     Replaces the depot positions and redoes the search.
        public void SetDepots(IEnumerable<Vector2> positions)
        {
            depots.Clear();
            depots.AddRange(positions ?? Enumerable.Empty<Vector2>());
            Recompute();
        }

        // -----------------------------------------------------------------------------------
        // Function: SetClosed
        // -----------------------------------------------------------------------------------
        // Description:
        // roadId|direction
        // road-9	Both directions of that road
        // road-9|1	Only the forward direction
        // road-9|-1	Only the reverse direction
        //     Replaces the closed roads and redoes the search. An ID matches a whole road ("road-9")
        //     or one direction of it ("road-9|1"). A shuttle already on a closed road may still drive
        //     off it; only entering one is blocked.
        public void SetClosed(IEnumerable<string> ids)
        {
            closed.Clear();
            foreach (var id in ids ?? Enumerable.Empty<string>()) closed.Add(id);
            Recompute();
        }

        private bool IsClosed(Edge e) => closed.Count > 0 && (closed.Contains(e.Key) || closed.Contains(e.RoadId));

        private void Recompute()
        {
            toDepot = new Dictionary<long, float>();
            if (depots.Count == 0 || nodes.Count == 0) return;
            var open = new SortedSet<(float Distance, long Node)>();

            void Relax(long node, float distance)
            {
                if (toDepot.TryGetValue(node, out var old))
                {
                    if (distance >= old) return;
                    open.Remove((old, node));
                }
                toDepot[node] = distance;
                open.Add((distance, node));
            }

            foreach (var depot in depots)
            {
                var near = nodes.OrderBy(n => Vector2.Distance(n.Value, depot)).Take(DepotEntrances).ToList();
                for (var i = 0; i < near.Count; i++)
                {
                    var distance = Vector2.Distance(near[i].Value, depot);
                    if (i == 0 || distance <= EntranceRadiusM) Relax(near[i].Key, distance);
                }
            }

            while (open.Count > 0)
            {
                var next = open.Min;
                open.Remove(next);
                if (!incoming.TryGetValue(next.Node, out var arriving)) continue;
                // Walking the road backwards: each road that ENDS here is reached from its start
                foreach (var edge in arriving)
                    if (!IsClosed(edge)) Relax(edge.From, next.Distance + edge.Length);
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: DistanceToDepot
        // -----------------------------------------------------------------------------------
        // Description:
        //     Shortest driving distance from a point to the nearest depot. With a heading, the way
        //     the shuttle is facing is preferred on a two-way road; without one the shorter way wins.
        //
        // Args:
        //     at (Vector2): Position in local metres.
        //     headingRad (float): Radians anticlockwise from east, or NaN when unknown.
        public Result DistanceToDepot(Vector2 at, float headingRad = float.NaN)
        {
            if (depots.Count == 0) return new Result { Reachable = false };

            var nearestDepot = depots.Min(d => Vector2.Distance(at, d));
            if (nearestDepot <= AtDepotRadiusM) return new Result { Reachable = true, DistanceM = nearestDepot };

            var facing = float.IsNaN(headingRad) ? (Vector2?)null : new Vector2(Mathf.Cos(headingRad), Mathf.Sin(headingRad));
            scratch.Clear();
            var nearestRoad = float.MaxValue;
            foreach (var edge in edges)
            {
                var best = new Candidate { Edge = edge, Lateral = float.MaxValue };
                Vector2 direction = Vector2.zero;
                for (var i = 0; i < edge.Points.Length - 1; i++)
                {
                    var a = edge.Points[i];
                    var ab = edge.Points[i + 1] - a;
                    var lengthSquared = ab.sqrMagnitude;
                    var t = lengthSquared < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(at - a, ab) / lengthSquared);
                    var lateral = Vector2.Distance(at, a + ab * t);
                    if (lateral >= best.Lateral) continue;
                    best.Lateral = lateral;
                    best.Remaining = edge.Length - (edge.Along[i] + Mathf.Sqrt(lengthSquared) * t);
                    direction = ab.normalized;
                }
                if (best.Lateral > SnapRadiusM) continue;
                best.Score = facing.HasValue && Vector2.Dot(direction, facing.Value) < 0 ? WrongWayPenaltyM : 0f;
                scratch.Add(best);
                if (best.Lateral < nearestRoad) nearestRoad = best.Lateral;
            }

            if (scratch.Count == 0) return new Result { Reachable = true, OffNetwork = true, DistanceM = nearestDepot };

            // Among the roads under the shuttle (a two-way road is two of them), the cheapest way home
            var result = new Result { Reachable = false, DistanceM = float.MaxValue };
            var bestCost = float.MaxValue;
            foreach (var c in scratch)
            {
                if (c.Lateral > nearestRoad + SnapTieM || !toDepot.TryGetValue(c.Edge.To, out var rest)) continue;
                var distance = c.Remaining + rest;
                if (distance + c.Score >= bestCost) continue;
                bestCost = distance + c.Score;
                result = new Result { Reachable = true, DistanceM = distance };
            }
            if (!result.Reachable) result.DistanceM = float.PositiveInfinity;
            return result;
        }
    }
}
