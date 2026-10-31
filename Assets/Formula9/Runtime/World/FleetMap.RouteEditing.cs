using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Formula9.World
{
    // The route editor on the map: every road the shuttle could use, the closure, the proposal and
    // the pins. Clicking a road proposes driving it; the backend plans and judges the route.
    public sealed partial class FleetMap
    {
        private const float RoadReachPoints = 12, PinReachPoints = 13, PinRadius = 11, ChevronSpacingPoints = 70;
        private static readonly Color Road = new Color(0.25f, 0.35f, 0.5f, 0.55f);
        private static readonly Color RoadNotCleared = new Color(0.72f, 0.5f, 0.1f, 0.85f);
        private static readonly Color Proposed = new Color(0.086f, 0.396f, 0.753f);
        private static readonly Color ProposedCasing = new Color(1, 1, 1, 0.9f);
        private static readonly Color Hover = new Color(0.086f, 0.396f, 0.753f, 0.5f);
        private static readonly Color CommittedInEdit = new Color(0.153f, 0.682f, 0.376f, 0.55f);

        private RouteDraftView draft;
        private List<DraftRoad> closedRoads;
        private DraftRoad hovered;
        private readonly Label hoverLabel = new Label { pickingMode = PickingMode.Ignore };
        private bool draftFramed;

        public event Action<DraftRoad, Vector2> RoadClicked;
        public event Action<int> WaypointClicked;

        private void BuildEditing()
        {
            hoverLabel.AddToClassList("map-hover");
            hoverLabel.style.display = DisplayStyle.None;
            Add(hoverLabel);
            RegisterCallback<PointerMoveEvent>(e => { if (!pressed) NoteHover(e.localPosition); });
            RegisterCallback<PointerLeaveEvent>(_ => SetHover(null, default));
        }

        // Shows a proposal on the map, or with null goes back to the plain fleet view.
        public void ShowDraft(RouteDraftView value)
        {
            var changedContext = draft?.Context != value?.Context;
            draft = value;
            if (changedContext) { hovered = null; hoverLabel.style.display = DisplayStyle.None; draftFramed = false; }
            // The legend would sit over the roads the operator has to read and click
            if (draft != null && !draftFramed) showLegend(false);
            if (draft != null && !draftFramed && contentRect.width >= 1) { FrameDraft(); draftFramed = true; }
            Show(snapshot);
        }

        private void FrameDraft()
        {
            pan = Vector2.zero; zoom = 1;
        }

        // Bring these roads into view, closer in if they are small on screen.
        public void Focus(IEnumerable<string> edgeIds)
        {
            if (draft == null) return;
            var points = edgeIds.SelectMany(id => draft.TryRoad(id, out var road) ? road.Points : Array.Empty<Vector2>()).ToList();
            if (points.Count == 0) return;
            var low = new Vector2(points.Min(p => p.x), points.Min(p => p.y));
            var high = new Vector2(points.Max(p => p.x), points.Max(p => p.y));
            var extent = Mathf.Max(high.x - low.x, high.y - low.y, 120);
            var wanted = Mathf.Min(contentRect.width, contentRect.height) * 0.45f / extent;
            zoom = Mathf.Clamp(wanted / FitScale, 0.5f, 40);
            var middle = Project((low.x + high.x) / 2, (low.y + high.y) / 2);
            pan += contentRect.size / 2 - middle;
            Refresh();
        }

        private void NoteHover(Vector2 point)
        {
            if (draft == null || !draft.Editable) { SetHover(null, default); return; }
            var road = RouteDraftView.Nearest(VisibleRoads(), m => Project(m.x, m.y), point, RoadReachPoints, out _);
            SetHover(road, point);
        }

        private void SetHover(DraftRoad road, Vector2 point)
        {
            if (road != hovered) { hovered = road; drawing.MarkDirtyRepaint(); }
            if (road == null) { hoverLabel.style.display = DisplayStyle.None; return; }
            hoverLabel.text = HoverText(road);
            hoverLabel.style.left = Mathf.Min(point.x + 14, Mathf.Max(0, contentRect.width - 300));
            hoverLabel.style.top = Mathf.Max(0, point.y - 30);
            hoverLabel.style.display = DisplayStyle.Flex;
        }

        private static string HoverText(DraftRoad road)
        {
            var name = string.IsNullOrEmpty(road.Name) ? "Road" : road.Name;
            if (road.Closed) return name + " · closed";
            return road.Fit == null || road.Fit == "PASS" ? name : name + " · not cleared for the shuttle";
        }

        private IEnumerable<DraftRoad> VisibleRoads()
        {
            var view = ViewInMetres();
            return draft.Roads.Where(r => r.Bounds.Overlaps(view));
        }

        private Rect ViewInMetres()
        {
            var a = Unproject(Vector2.zero); var b = Unproject(contentRect.size);
            return Rect.MinMaxRect((float)Math.Min(a.X, b.X), (float)Math.Min(a.Y, b.Y), (float)Math.Max(a.X, b.X), (float)Math.Max(a.Y, b.Y));
        }

        private bool ClickEditor(Vector2 point)
        {
            if (draft == null || !draft.Editable) return false;
            for (var i = draft.Waypoints.Count - 1; i >= 0; i--)
                if (Vector2.Distance(Project(draft.Waypoints[i].X, draft.Waypoints[i].Y), point) <= PinReachPoints)
                {
                    WaypointClicked?.Invoke(i);
                    return true;
                }
            var road = RouteDraftView.Nearest(VisibleRoads(), m => Project(m.x, m.y), point, RoadReachPoints, out var onRoad);
            if (road != null) RoadClicked?.Invoke(road, onRoad);
            return true;
        }

        private void DrawRoadLayer(Painter2D painter)
        {
            var view = ViewInMetres();
            StrokeRoads(painter, view, r => !r.Closed && (r.Fit == null || r.Fit == "PASS"), Road, 2);
            StrokeRoads(painter, view, r => !r.Closed && r.Fit != null && r.Fit != "PASS", RoadNotCleared, 2);
            StrokeRoads(painter, view, r => r.Closed, new Color(RoadClosed.r, RoadClosed.g, RoadClosed.b, 0.3f), 18);
            StrokeRoads(painter, view, r => r.Closed, RoadClosed, 5);
            foreach (var road in draft.Roads.Where(r => r.Closed && !r.OnTwinSide))
            {
                var at = Project(road.Points[road.Points.Length / 2].x, road.Points[road.Points.Length / 2].y);
                if (contentRect.Contains(at)) ClosureSign(painter, at);
            }
        }

        private void StrokeRoads(Painter2D painter, Rect view, Func<DraftRoad, bool> which, Color color, float width)
        {
            painter.strokeColor = color; painter.lineWidth = width; painter.lineJoin = LineJoin.Round; painter.lineCap = LineCap.Round;
            painter.BeginPath();
            foreach (var road in draft.Roads)
            {
                if (road.OnTwinSide || !which(road) || !road.Bounds.Overlaps(view)) continue;
                painter.MoveTo(Project(road.Points[0].x, road.Points[0].y));
                for (var i = 1; i < road.Points.Length; i++) painter.LineTo(Project(road.Points[i].x, road.Points[i].y));
            }
            painter.Stroke();
        }

        // The road stays closed after a route goes round it, so the fleet view keeps showing it.
        public void ShowClosedRoads(RouteDraftView value)
        {
            closedRoads = value?.Roads.Where(r => r.Closed && !r.OnTwinSide).ToList();
            drawing.MarkDirtyRepaint();
        }

        private void DrawClosedRoads(Painter2D painter)
        {
            if (closedRoads == null) return;
            foreach (var road in closedRoads)
            {
                var path = road.Points.Select(p => Project(p.x, p.y)).ToArray();
                Line(painter, path, new Color(RoadClosed.r, RoadClosed.g, RoadClosed.b, 0.3f), 18);
                Line(painter, path, RoadClosed, 5);
                ClosureSign(painter, path[path.Length / 2]);
            }
        }

        private static void ClosureSign(Painter2D painter, Vector2 at)
        {
            Dot(painter, at, Color.white, 11, false);
            Dot(painter, at, RoadClosed, 9, false);
            Line(painter, new[] { at + new Vector2(-4.5f, -4.5f), at + new Vector2(4.5f, 4.5f) }, Color.white, 2.2f);
            Line(painter, new[] { at + new Vector2(-4.5f, 4.5f), at + new Vector2(4.5f, -4.5f) }, Color.white, 2.2f);
        }

        // The proposal over everything but the markers: a casing so it reads on the map, arrows for its
        // direction, and the segments a check is about pulsing in red.
        private void DrawProposal(Painter2D painter)
        {
            if (hovered != null) Line(painter, hovered.Points.Select(p => Project(p.x, p.y)).ToArray(), Hover, 8);
            var paths = draft.ProposalPaths().Select(path => (path.EdgeId, Points: path.Points.Select(p => Project(p.x, p.y)).ToArray())).ToList();
            foreach (var path in paths) Line(painter, path.Points, ProposedCasing, 9);
            foreach (var path in paths) Line(painter, path.Points, Proposed, 5);
            Chevrons(painter, paths.Select(p => p.Points));
            var pulse = 0.5f + 0.5f * Mathf.Sin(Time.realtimeSinceStartup * 5);
            foreach (var id in draft.Highlighted)
            {
                if (!draft.TryRoad(id, out var road)) continue;
                var points = road.Points.Select(p => Project(p.x, p.y)).ToArray();
                Line(painter, points, new Color(RoadClosed.r, RoadClosed.g, RoadClosed.b, 0.25f + 0.3f * pulse), 22);
                Line(painter, points, RoadClosed, 6);
            }
        }

        private static void Chevrons(Painter2D painter, IEnumerable<Vector2[]> paths)
        {
            var until = ChevronSpacingPoints / 2;
            foreach (var path in paths)
                for (var i = 1; i < path.Length; i++)
                {
                    var along = path[i] - path[i - 1];
                    var length = along.magnitude;
                    if (length <= 0) continue;
                    var direction = along / length;
                    var side = new Vector2(-direction.y, direction.x);
                    var at = 0f;
                    while (until < length - at)
                    {
                        at += until;
                        var tip = path[i - 1] + direction * at;
                        Line(painter, new[] { tip - direction * 5 + side * 4, tip, tip - direction * 5 - side * 4 }, Color.white, 1.6f);
                        until = ChevronSpacingPoints;
                    }
                    until -= length - at;
                }
        }

        private void DrawPins(Painter2D painter)
        {
            foreach (var stop in draft.Context.Stops)
            {
                var at = Project(stop.X, stop.Y);
                Dot(painter, at, Color.white, 9, false);
                Dot(painter, at, StopColor, 7, false);
            }
            foreach (var waypoint in draft.Waypoints)
            {
                var at = Project(waypoint.X, waypoint.Y);
                Dot(painter, at, Color.white, PinRadius + 2, false);
                Dot(painter, at, Proposed, PinRadius, false);
            }
        }

        // The pins' numbers and the stops' names; IDs stay out of it.
        private void PlaceDraftLabels()
        {
            for (var i = 0; i < draft.Waypoints.Count; i++)
            {
                var at = Project(draft.Waypoints[i].X, draft.Waypoints[i].Y);
                var number = new Label((i + 1).ToString()) { pickingMode = PickingMode.Ignore };
                number.AddToClassList("map-pin-number");
                number.style.left = at.x - PinRadius; number.style.top = at.y - PinRadius;
                labels.Add(number);
            }
            foreach (var stop in draft.Context.Stops) PlaceLabel(stop.Name, Project(stop.X, stop.Y), LabelStyle.Beside, StopColor);
        }
    }
}
