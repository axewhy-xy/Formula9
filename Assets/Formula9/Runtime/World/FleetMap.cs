using System;
using System.Collections.Generic;
using System.Linq;
using Formula9.Contracts;
using UnityEngine;
using UnityEngine.UIElements;

namespace Formula9.World
{
    // An operational planar view, also used for the pre-trip graph. Coordinates and routes
    // come from the backend; the client never creates substitute road geometry.
    public sealed class FleetMap : VisualElement
    {
        private FullSnapshot snapshot;
        private RouteEditorContext context;
        private RouteEditPreview preview;
        private readonly VisualElement labels = new VisualElement { pickingMode = PickingMode.Ignore };
        private Vector2 min, max, pan, lastPointer;
        private float zoom = 1;
        private bool dragging;
        public string SelectedId { get; set; }
        public event Action<string> VehicleSelected;
        public event Action<long> NodeSelected;

        public FleetMap()
        {
            pickingMode = PickingMode.Position;
            labels.style.position = UnityEngine.UIElements.Position.Absolute;
            labels.style.left = labels.style.top = labels.style.right = labels.style.bottom = 0;
            Add(labels);
            generateVisualContent += Draw;
            RegisterCallback<GeometryChangedEvent>(_ => RefreshLabels());
            RegisterCallback<WheelEvent>(e => { zoom = Mathf.Clamp(zoom * Mathf.Exp(-e.delta.y * 0.06f), 0.5f, 12); RefreshLabels(); e.StopPropagation(); });
            RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button == 2) { dragging = true; lastPointer = e.localPosition; this.CapturePointer(e.pointerId); e.StopPropagation(); }
                if (e.button != 0) return;
                var point = new Vector2(e.localPosition.x, e.localPosition.y);
                if (context != null)
                {
                    var nearest = context.Nodes.OrderBy(n => Vector2.Distance(Project(n.X, n.Y), point)).FirstOrDefault();
                    if (nearest != null && Vector2.Distance(Project(nearest.X, nearest.Y), point) <= 14) NodeSelected?.Invoke(nearest.NodeId);
                }
                else if (snapshot != null)
                {
                    var nearest = snapshot.Vehicles.OrderBy(v => Vector2.Distance(Project(v.Position.XM, v.Position.YM), point)).FirstOrDefault();
                    if (nearest != null && Vector2.Distance(Project(nearest.Position.XM, nearest.Position.YM), point) <= 18)
                    { SelectedId = nearest.VehicleId; VehicleSelected?.Invoke(SelectedId); RefreshLabels(); }
                }
                e.StopPropagation();
            });
            RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!dragging) return;
                Vector2 next = e.localPosition; pan += next - lastPointer; lastPointer = next; RefreshLabels();
            });
            RegisterCallback<PointerUpEvent>(e => { if (e.button == 2) { dragging = false; this.ReleasePointer(e.pointerId); } });
            RegisterCallback<PointerCaptureOutEvent>(_ => dragging = false);
            style.overflow = Overflow.Hidden;
        }

        public void Show(FullSnapshot value, RouteEditorContext graph = null, RouteEditPreview proposal = null)
        {
            if (snapshot?.SessionId != value?.SessionId || context?.RouteId != graph?.RouteId) { pan = Vector2.zero; zoom = 1; }
            snapshot = value; context = graph; preview = proposal;
            var points = new List<Vector2>();
            if (context != null) points.AddRange(context.Nodes.Select(n => new Vector2((float)n.X, (float)n.Y)));
            else if (snapshot != null)
            {
                points.AddRange(snapshot.Routes.SelectMany(r => r.EdgePathsM).SelectMany(p => p).Select(p => new Vector2((float)p.XM, (float)p.YM)));
                points.AddRange(snapshot.Vehicles.Select(v => new Vector2((float)v.Position.XM, (float)v.Position.YM)));
                points.AddRange(snapshot.Depots.Select(d => new Vector2((float)d.Position.XM, (float)d.Position.YM)));
            }
            min = points.Count == 0 ? Vector2.zero : new Vector2(points.Min(p => p.x), points.Min(p => p.y));
            max = points.Count == 0 ? Vector2.one : new Vector2(points.Max(p => p.x), points.Max(p => p.y));
            RefreshLabels();
        }

        private Vector2 Project(double x, double y)
        {
            var width = Mathf.Max(1, contentRect.width - 60); var height = Mathf.Max(1, contentRect.height - 60);
            var scale = Mathf.Min(width / Mathf.Max(1, max.x - min.x), height / Mathf.Max(1, max.y - min.y)) * zoom;
            var center = (min + max) / 2;
            return new Vector2(contentRect.width / 2 + ((float)x - center.x) * scale, contentRect.height / 2 - ((float)y - center.y) * scale) + pan;
        }

        private void RefreshLabels()
        {
            labels.Clear();
            if (context != null)
                foreach (var node in context.Nodes) AddLabel(node.NodeId.ToString(), Project(node.X, node.Y));
            else if (snapshot != null)
            {
                foreach (var vehicle in snapshot.Vehicles)
                    AddLabel(vehicle.VehicleId + (snapshot.Incidents.Any(i => i.VehicleId == vehicle.VehicleId && i.State != "RESOLVED") ? " !" : ""), Project(vehicle.Position.XM, vehicle.Position.YM));
                foreach (var depot in snapshot.Depots) AddLabel(depot.DepotId + " / " + string.Join(", ", depot.ChargerIds), Project(depot.Position.XM, depot.Position.YM));
            }
            MarkDirtyRepaint();
        }

        private void AddLabel(string text, Vector2 point)
        {
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.style.position = UnityEngine.UIElements.Position.Absolute; label.style.left = point.x + 7; label.style.top = point.y + 3;
            label.style.fontSize = 11; label.style.color = Color.white;
            labels.Add(label);
        }

        private void Draw(MeshGenerationContext mesh)
        {
            var painter = mesh.painter2D;
            if (context != null)
            {
                var nodes = context.Nodes.ToDictionary(n => n.NodeId);
                foreach (var edge in context.Edges)
                {
                    if (!nodes.TryGetValue(edge.Source, out var a) || !nodes.TryGetValue(edge.Target, out var b)) continue;
                    var start = Project(a.X, a.Y); var end = Project(b.X, b.Y);
                    Line(painter, new[] { start, end }, edge.Closed ? Color.red : new Color(0.5f, 0.6f, 0.7f), 2);
                    var direction = (end - start).normalized; var tip = Vector2.Lerp(start, end, 0.65f);
                    Line(painter, new[] { tip - direction * 7 + new Vector2(-direction.y, direction.x) * 4, tip, tip - direction * 7 - new Vector2(-direction.y, direction.x) * 4 }, Color.gray, 1);
                }
                foreach (var node in context.Nodes) Dot(painter, Project(node.X, node.Y), node.NodeId == context.StartNodeId || node.NodeId == context.DestinationNodeId ? Color.green : Color.white, 4);
            }
            else if (snapshot != null)
            {
                var closures = new HashSet<string>(snapshot.Disruptions.Where(d => d.ActiveFromTick <= snapshot.Tick && (d.ActiveUntilTick == null || snapshot.Tick < d.ActiveUntilTick)).SelectMany(d => d.AffectedEdgeIds));
                foreach (var route in snapshot.Routes)
                    for (var i = 0; i < route.EdgePathsM.Count; i++)
                        Line(painter, route.EdgePathsM[i].Select(p => Project(p.XM, p.YM)).ToArray(), i < route.DirectedEdgeIds.Count && closures.Contains(route.DirectedEdgeIds[i]) ? Color.red : new Color(0.4f, 0.6f, 0.7f), 3);
                foreach (var depot in snapshot.Depots) Dot(painter, Project(depot.Position.XM, depot.Position.YM), Color.yellow, 7);
                foreach (var vehicle in snapshot.Vehicles)
                    Dot(painter, Project(vehicle.Position.XM, vehicle.Position.YM), vehicle.VehicleId == SelectedId ? Color.yellow : Color.cyan, 6);
            }
            if (preview != null) Line(painter, preview.Polyline.Select(p => Project(p.X, p.Y)).ToArray(), Color.green, 4);
        }

        private static void Line(Painter2D painter, Vector2[] points, Color color, float width)
        {
            if (points.Length < 2) return;
            painter.strokeColor = color; painter.lineWidth = width; painter.BeginPath(); painter.MoveTo(points[0]);
            foreach (var point in points.Skip(1)) painter.LineTo(point);
            painter.Stroke();
        }
        private static void Dot(Painter2D painter, Vector2 point, Color color, float radius)
        {
            painter.fillColor = color; painter.BeginPath(); painter.Arc(point, radius, 0, 360); painter.Fill();
        }
    }
}
