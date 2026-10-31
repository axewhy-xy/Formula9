using System;
using System.Collections.Generic;
using System.Linq;
using Formula9.Contracts;
using UnityEngine;

namespace Formula9.World
{
    // A road or point the operator picked. The ID behind it stays internal: people see the road's name.
    public sealed class RouteWaypoint
    {
        public string EdgeId;
        public long NodeId;
        public double X, Y;
        public string Road;
    }

    // One road as both views draw it, in local metres.
    public sealed class DraftRoad
    {
        public string EdgeId, Name, Fit;
        public bool Closed, OnTwinSide;
        public Vector2[] Points;
        public Rect Bounds;
    }

    // What the operator is proposing, in the shape the 2D map and the 3D city both show. All road
    // shapes and verdicts come from the backend; this only arranges them for drawing.
    public sealed class RouteDraftView
    {
        public RouteEditorContext Context { get; }
        public IReadOnlyList<DraftRoad> Roads { get; }
        private readonly Dictionary<string, DraftRoad> byId;

        public RouteEditPreview Preview;
        public List<RouteWaypoint> Waypoints = new List<RouteWaypoint>();
        public HashSet<string> Highlighted = new HashSet<string>();
        public bool Editable;

        public RouteDraftView(RouteEditorContext context)
        {
            Context = context;
            var nodes = context.Nodes.ToDictionary(n => n.NodeId);
            var all = new List<DraftRoad>();
            var ids = new HashSet<string>(context.Edges.Select(e => e.EdgeId));
            foreach (var edge in context.Edges)
            {
                Vector2[] points;
                if (edge.Path != null && edge.Path.Count >= 2) points = edge.Path.Select(p => new Vector2((float)p.X, (float)p.Y)).ToArray();
                else if (nodes.TryGetValue(edge.Source, out var a) && nodes.TryGetValue(edge.Target, out var b))
                    points = new[] { new Vector2((float)a.X, (float)a.Y), new Vector2((float)b.X, (float)b.Y) };
                else continue;
                all.Add(new DraftRoad
                {
                    EdgeId = edge.EdgeId, Name = edge.RoadName, Fit = edge.Fit, Closed = edge.Closed, Points = points, Bounds = BoundsOf(points),
                    // A two-way road is drawn once; either direction picks it
                    OnTwinSide = edge.EdgeId.EndsWith(":reverse", StringComparison.Ordinal) && ids.Contains(edge.EdgeId.Substring(0, edge.EdgeId.Length - "reverse".Length) + "forward"),
                });
            }
            Roads = all;
            byId = all.ToDictionary(r => r.EdgeId);
        }

        // A stop's name for people, as the backend's stop_label says it: scenario stops are <code>-<slug>.
        public static string StopName(string stopId)
        {
            var dash = stopId.IndexOf('-');
            var named = dash > 0 && dash + 1 < stopId.Length && stopId.Take(dash).All(char.IsDigit);
            var words = (named ? stopId.Substring(dash + 1) : stopId).Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", words.Select(word => word.Length <= 3 && word.Any(char.IsDigit) ? word.ToUpperInvariant() : char.ToUpperInvariant(word[0]) + word.Substring(1).ToLowerInvariant()));
        }

        // Roads of an operating network are picked by name; a route with only a curated graph by its nodes.
        public bool PicksRoads => Context.Edges.Any(e => e.Fit != null);

        public bool TryRoad(string edgeId, out DraftRoad road) => byId.TryGetValue(edgeId ?? "", out road);

        // Each edge of the proposal, as drawn.
        public List<(string EdgeId, Vector2[] Points)> ProposalPaths()
        {
            var result = new List<(string, Vector2[])>();
            if (Preview == null) return result;
            if (Preview.EdgePaths != null && Preview.EdgePaths.Count == Preview.DirectedEdgeIds.Count)
                for (var i = 0; i < Preview.EdgePaths.Count; i++)
                    result.Add((Preview.DirectedEdgeIds[i], Preview.EdgePaths[i].Select(p => new Vector2((float)p.X, (float)p.Y)).ToArray()));
            else if (Preview.Polyline != null)
                for (var i = 0; i + 1 < Preview.Polyline.Count && i < Preview.DirectedEdgeIds.Count; i++)
                    result.Add((Preview.DirectedEdgeIds[i], new[]
                    {
                        new Vector2((float)Preview.Polyline[i].X, (float)Preview.Polyline[i].Y),
                        new Vector2((float)Preview.Polyline[i + 1].X, (float)Preview.Polyline[i + 1].Y),
                    }));
            return result;
        }

        // The road nearest a point on screen, within reach. Project is the view's metres-to-screen mapping.
        public static DraftRoad Nearest(IEnumerable<DraftRoad> roads, Func<Vector2, Vector2> project, Vector2 point, float reach, out Vector2 onRoad)
        {
            DraftRoad best = null;
            var bestDistance = reach;
            onRoad = default;
            foreach (var road in roads)
            {
                if (road.OnTwinSide) continue;
                var previous = project(road.Points[0]);
                for (var i = 1; i < road.Points.Length; i++)
                {
                    var next = project(road.Points[i]);
                    var along = next - previous;
                    var share = along.sqrMagnitude > 0 ? Mathf.Clamp01(Vector2.Dot(point - previous, along) / along.sqrMagnitude) : 0;
                    var distance = Vector2.Distance(point, previous + along * share);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = road;
                        onRoad = Vector2.Lerp(road.Points[i - 1], road.Points[i], share);
                    }
                    previous = next;
                }
            }
            return best;
        }

        // Every point worth keeping in view: the route, its stops and the closures.
        public List<Vector2> FocusPoints()
        {
            var points = new List<Vector2>();
            var committed = new HashSet<string>(Context.OriginalDirectedEdgeIds ?? new List<string>());
            foreach (var road in Roads)
                if (road.Closed || committed.Contains(road.EdgeId)) points.AddRange(road.Points);
            points.AddRange(Context.Stops.Select(s => new Vector2((float)s.X, (float)s.Y)));
            if (points.Count == 0) points.AddRange(Context.Nodes.Select(n => new Vector2((float)n.X, (float)n.Y)));
            return points;
        }

        private static Rect BoundsOf(Vector2[] points)
        {
            var min = points[0]; var max = points[0];
            foreach (var point in points) { min = Vector2.Min(min, point); max = Vector2.Max(max, point); }
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }
    }
}
