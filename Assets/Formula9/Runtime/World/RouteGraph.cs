using System;
using System.Collections.Generic;
using System.Linq;
using Formula9.Contracts;
using UnityEngine;
using UnityEngine.UIElements;

namespace Formula9.World
{
    // The pre-trip route graph: nodes and directed edges from the backend. Drag a node onto an
    // interior node to choose a via waypoint, or click an interior node to add it. Port of the Qt
    // RouteGraphCanvas; it draws what the backend sent and never invents road geometry.
    [UxmlElement]
    public sealed partial class RouteGraph : VisualElement
    {
        private const float GrabDistance = 18, NodeRadius = 6, LabelColumn = 330, LabelGap = 16;
        private const int MaxViaNodes = 16;
        private static readonly Color Ink = new Color(0.118f, 0.165f, 0.18f);
        private static readonly Color Closed = new Color(0.725f, 0.255f, 0.157f);
        private static readonly Color Proposal = new Color(0.157f, 0.451f, 0.745f);
        private static readonly Color Via = new Color(0.922f, 0.725f, 0.255f);
        private static readonly Color Leader = new Color(0.62f, 0.67f, 0.68f);

        private RouteEditorContext context;
        private RouteEditPreview preview;
        private List<long> via = new List<long>();
        private long? dragging;
        private Vector2 dragPoint;
        private readonly VisualElement labels = new VisualElement { pickingMode = PickingMode.Ignore };
        private readonly List<(Vector2 From, Vector2 To)> leaders = new List<(Vector2, Vector2)>();

        // The whole via list after a drag.
        public event Action<List<long>> Edited;
        // An interior node was clicked.
        public event Action<long> NodeClicked;

        public RouteGraph()
        {
            pickingMode = PickingMode.Position;
            style.overflow = Overflow.Hidden;
            labels.style.position = UnityEngine.UIElements.Position.Absolute;
            labels.style.left = labels.style.top = labels.style.right = labels.style.bottom = 0;
            Add(labels);
            generateVisualContent += Draw;
            RegisterCallback<GeometryChangedEvent>(_ => Refresh());
            RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                var node = Nearest(e.localPosition);
                if (node == null) return;
                dragging = node; dragPoint = e.localPosition;
                this.CapturePointer(e.pointerId);
                e.StopPropagation();
            });
            RegisterCallback<PointerMoveEvent>(e =>
            {
                if (dragging == null) return;
                dragPoint = e.localPosition;
                MarkDirtyRepaint();
            });
            RegisterCallback<PointerUpEvent>(e =>
            {
                if (e.button != 0 || dragging == null) return;
                var from = dragging.Value;
                dragging = null;
                this.ReleasePointer(e.pointerId);
                Drop(from, Nearest(e.localPosition));
                MarkDirtyRepaint();
            });
            RegisterCallback<PointerCaptureOutEvent>(_ => { dragging = null; MarkDirtyRepaint(); });
        }

        public void Show(RouteEditorContext graph, RouteEditPreview proposal, List<long> viaNodes)
        {
            context = graph; preview = proposal; via = viaNodes ?? new List<long>();
            Refresh();
        }

        private bool IsEnd(long id) => context != null && (id == context.StartNodeId || id == context.DestinationNodeId);

        private void Drop(long from, long? target)
        {
            if (target == null || IsEnd(target.Value)) return;
            if (target == from) { NodeClicked?.Invoke(from); return; }
            var result = new List<long>(via);
            var index = result.IndexOf(from);
            if (index >= 0) result[index] = target.Value;
            else if (result.Count < MaxViaNodes) result.Add(target.Value);
            else return;
            Edited?.Invoke(result);
        }

        private long? Nearest(Vector2 point)
        {
            if (context == null) return null;
            foreach (var node in context.Nodes)
                if (Vector2.Distance(point, ToScreen(node.X, node.Y)) <= GrabDistance) return node.NodeId;
            return null;
        }

        private Vector2 ToScreen(double x, double y)
        {
            double minX = context.Nodes.Min(n => n.X), maxX = context.Nodes.Max(n => n.X);
            double minY = context.Nodes.Min(n => n.Y), maxY = context.Nodes.Max(n => n.Y);
            var plotWidth = Math.Max(1, contentRect.width - LabelColumn);
            var scale = Math.Min(plotWidth / Math.Max(1, maxX - minX), (contentRect.height - 80) / Math.Max(1, maxY - minY));
            return new Vector2((float)(40 + (plotWidth - (maxX - minX) * scale) / 2 + (x - minX) * scale),
                (float)(contentRect.height - 40 - (y - minY) * scale));
        }

        private void Refresh()
        {
            labels.Clear();
            leaders.Clear();
            MarkDirtyRepaint();
            if (contentRect.width < 1 || contentRect.height < 1) return;
            if (context == null || context.Nodes.Count == 0)
            {
                var hint = AddLabel("Select a route to load its graph.", Vector2.zero, Ink);
                hint.style.left = 0; hint.style.right = 0; hint.style.top = contentRect.height / 2 - 10;
                hint.style.unityTextAlign = TextAnchor.MiddleCenter;
                return;
            }
            var points = context.Nodes.ToDictionary(n => n.NodeId, n => ToScreen(n.X, n.Y));
            foreach (var edge in context.Edges.Where(e => e.Closed))
                if (points.TryGetValue(edge.Source, out var a) && points.TryGetValue(edge.Target, out var b))
                    AddLabel("× CLOSED", (a + b) / 2 + new Vector2(-70, -20), Closed);
            // IDs sit in a column beside the plot, with leader lines, so they stay readable when real
            // graph nodes cluster near a junction.
            var labelLeft = points.Values.Max(p => p.x) + 24;
            var previous = 0f;
            var ordered = context.Nodes.OrderBy(n => points[n.NodeId].y).ToList();
            var baselines = new List<float>();
            foreach (var node in ordered)
            {
                previous = Mathf.Max(points[node.NodeId].y - 9, previous + LabelGap);
                baselines.Add(previous);
            }
            var shift = Mathf.Max(0, previous - contentRect.height + 18);
            for (var i = 0; i < ordered.Count; i++)
            {
                var node = ordered[i];
                var text = node.NodeId.ToString();
                if (node.NodeId == context.StartNodeId) text += " (start)";
                if (node.NodeId == context.DestinationNodeId) text += " (stop/end)";
                var at = new Vector2(labelLeft, baselines[i] - shift - 8);
                AddLabel(text, at, Ink);
                leaders.Add((points[node.NodeId] + new Vector2(NodeRadius + 1, 0), at + new Vector2(-4, 8)));
            }
        }

        private Label AddLabel(string text, Vector2 at, Color color)
        {
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.style.position = UnityEngine.UIElements.Position.Absolute;
            label.style.left = at.x; label.style.top = at.y;
            label.style.fontSize = 12; label.style.color = color;
            label.style.paddingLeft = label.style.paddingRight = 0;
            labels.Add(label);
            return label;
        }

        private void Draw(MeshGenerationContext mesh)
        {
            if (context == null || context.Nodes.Count == 0 || contentRect.width < 1 || contentRect.height < 1) return;
            var p = mesh.painter2D;
            var points = context.Nodes.ToDictionary(n => n.NodeId, n => ToScreen(n.X, n.Y));
            var committed = new HashSet<string>(context.OriginalDirectedEdgeIds ?? new List<string>());
            foreach (var edge in context.Edges)
            {
                if (!points.TryGetValue(edge.Source, out var a) || !points.TryGetValue(edge.Target, out var b)) continue;
                var color = edge.Closed ? Closed : Ink;
                var width = committed.Contains(edge.EdgeId) ? 4f : 1f;
                if (edge.Closed) Dashed(p, a, b, color, width, 8, 6);
                else Segment(p, a, b, color, width);
                // An arrow at the middle shows the edge's direction
                var mid = (a + b) / 2;
                var angle = Mathf.Atan2(b.y - a.y, b.x - a.x);
                Segment(p, mid, mid - new Vector2(Mathf.Cos(angle - 0.5f), Mathf.Sin(angle - 0.5f)) * 10, color, 1);
                Segment(p, mid, mid - new Vector2(Mathf.Cos(angle + 0.5f), Mathf.Sin(angle + 0.5f)) * 10, color, 1);
            }
            if (preview?.NodeIds != null)
                for (var i = 1; i < preview.NodeIds.Count; i++)
                    if (points.TryGetValue(preview.NodeIds[i - 1], out var a) && points.TryGetValue(preview.NodeIds[i], out var b))
                        Dashed(p, a, b, Proposal, 3, 12, 6);
            if (dragging != null && points.TryGetValue(dragging.Value, out var held)) Dashed(p, held, dragPoint, Ink, 1, 3, 3);
            foreach (var (from, to) in leaders) Segment(p, from, to, Leader, 1);
            foreach (var node in context.Nodes)
            {
                p.fillColor = via.Contains(node.NodeId) ? Via : Color.white;
                p.strokeColor = Ink; p.lineWidth = 1.2f;
                p.BeginPath(); p.Arc(points[node.NodeId], NodeRadius, 0, 360); p.Fill(); p.Stroke();
            }
        }

        private static void Segment(Painter2D p, Vector2 a, Vector2 b, Color color, float width)
        {
            p.strokeColor = color; p.lineWidth = width;
            p.BeginPath(); p.MoveTo(a); p.LineTo(b); p.Stroke();
        }

        private static void Dashed(Painter2D p, Vector2 a, Vector2 b, Color color, float width, float dash, float gap)
        {
            var length = Vector2.Distance(a, b);
            if (length < 0.01f) return;
            var along = (b - a) / length;
            for (var at = 0f; at < length; at += dash + gap)
                Segment(p, a + along * at, a + along * Mathf.Min(at + dash, length), color, width);
        }
    }
}
