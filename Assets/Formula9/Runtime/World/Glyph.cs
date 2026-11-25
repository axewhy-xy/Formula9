using UnityEngine;
using UnityEngine.UIElements;

namespace Formula9.World
{
    public enum GlyphKind { Bus, Pin, Warning, Plug, Document, User, Lock, Eye, Fit, Compass, Info, LogOut, Power, Refresh, Trash, Archive, Clock, Disk, Bell, BellOff }

    // A small line icon drawn in code, as the Qt client's AppShell glyphs are, so it stays sharp at
    // any scale and needs no image asset. It never takes clicks; whatever holds it does.
    [UxmlElement]
    public sealed partial class Glyph : VisualElement
    {
        private GlyphKind kind;
        private Color tint = Color.white;

        [UxmlAttribute("kind")] public GlyphKind Kind { get => kind; set { kind = value; MarkDirtyRepaint(); } }
        [UxmlAttribute("tint")] public Color Tint { get => tint; set { tint = value; MarkDirtyRepaint(); } }
        [UxmlAttribute("size")] public float Size { get => style.width.value.value; set { style.width = style.height = value; } }

        public Glyph() : this(GlyphKind.Bus, Color.white) { }

        public Glyph(GlyphKind kind, Color tint, float size = 22)
        {
            this.kind = kind; this.tint = tint;
            pickingMode = PickingMode.Ignore;
            Size = size;
            style.flexShrink = 0;
            generateVisualContent += Draw;
        }

        // The front of a bus, centred on a point, in the shuttle's colour.
        public static readonly Color ChargerColor = new Color(0.98f, 0.75f, 0.14f);

        // A lightning bolt on an amber disc, centred on the charger.
        public static void DrawCharger(Painter2D painter, Vector2 center, float size)
        {
            var radius = size / 2;
            painter.fillColor = Color.white;
            painter.BeginPath(); painter.Arc(center, radius + 2, 0, 360); painter.Fill();
            painter.fillColor = ChargerColor;
            painter.BeginPath(); painter.Arc(center, radius, 0, 360); painter.Fill();
            painter.fillColor = new Color(0.13f, 0.13f, 0.16f);
            painter.BeginPath();
            foreach (var (x, y, first) in new[] { (0.16f, -0.72f, true), (-0.46f, 0.12f, false), (-0.06f, 0.12f, false), (-0.16f, 0.72f, false), (0.46f, -0.14f, false), (0.06f, -0.14f, false) })
            {
                var point = center + new Vector2(x, y) * radius;
                if (first) painter.MoveTo(point); else painter.LineTo(point);
            }
            painter.ClosePath(); painter.Fill();
        }

        public static void DrawBus(Painter2D painter, Vector2 center, float size, Color body)
        {
            var s = size / 36;
            Vector2 At(float x, float y) => center + new Vector2((x - 18) * s, (y - 18) * s);
            painter.fillColor = new Color(body.r * 0.7f, body.g * 0.7f, body.b * 0.7f, body.a);
            painter.BeginPath(); painter.Arc(At(11, 32), 3 * s, 0, 360); painter.Fill();
            painter.BeginPath(); painter.Arc(At(25, 32), 3 * s, 0, 360); painter.Fill();
            Rounded(painter, At(5, 3), 26 * s, 28 * s, 6 * s, body); painter.Fill();
            painter.strokeColor = Color.white; painter.lineWidth = Mathf.Max(1, 1.5f * s); painter.Stroke();
            Rounded(painter, At(9, 7), 18 * s, 10 * s, 2 * s, Color.white); painter.Fill();
        }

        // Google Maps' transit blue, so a bus stop reads as one at a glance.
        public static readonly Color BusStopColor = new Color(0.102f, 0.451f, 0.91f);

        // A bus stop as Google Maps draws one: a white bus on a blue rounded square, centred on the stop.
        // The white rim keeps it visible on the street map's own blue roads and water.
        public static void DrawBusStop(Painter2D painter, Vector2 center, float size)
        {
            var s = size / 16;
            Vector2 At(float x, float y) => center + new Vector2((x - 8) * s, (y - 8) * s);
            Rounded(painter, At(-1, -1), 18 * s, 18 * s, 4 * s, Color.white); painter.Fill();
            Rounded(painter, At(0, 0), 16 * s, 16 * s, 3 * s, BusStopColor); painter.Fill();
            // The bus: its body, then the windscreen cut out in blue, its headlights and its two wheels
            Rounded(painter, At(4, 2.5f), 8 * s, 9 * s, 1.5f * s, Color.white); painter.Fill();
            Rounded(painter, At(5, 4), 6 * s, 3.5f * s, 0.6f * s, BusStopColor); painter.Fill();
            painter.fillColor = BusStopColor;
            painter.BeginPath(); painter.Arc(At(5.6f, 9.6f), 0.7f * s, 0, 360); painter.Fill();
            painter.BeginPath(); painter.Arc(At(10.4f, 9.6f), 0.7f * s, 0, 360); painter.Fill();
            Rounded(painter, At(4.5f, 11), 2 * s, 2.5f * s, 0.6f * s, Color.white); painter.Fill();
            Rounded(painter, At(9.5f, 11), 2 * s, 2.5f * s, 0.6f * s, Color.white); painter.Fill();
        }

        private static void Rounded(Painter2D painter, Vector2 corner, float width, float height, float radius, Color fill)
        {
            painter.fillColor = fill;
            painter.BeginPath();
            painter.MoveTo(corner + new Vector2(radius, 0));
            painter.ArcTo(corner + new Vector2(width, 0), corner + new Vector2(width, height), radius);
            painter.ArcTo(corner + new Vector2(width, height), corner + new Vector2(0, height), radius);
            painter.ArcTo(corner + new Vector2(0, height), corner, radius);
            painter.ArcTo(corner, corner + new Vector2(width, 0), radius);
            painter.ClosePath();
        }

        private void Draw(MeshGenerationContext mesh)
        {
            var p = mesh.painter2D;
            var s = contentRect.width;
            if (s < 1) return;
            Vector2 At(float x, float y) => new Vector2(x * s, y * s);
            void Lines(bool closed, params Vector2[] points)
            {
                p.BeginPath(); p.MoveTo(points[0]);
                for (var i = 1; i < points.Length; i++) p.LineTo(points[i]);
                if (closed) p.ClosePath();
                p.Stroke();
            }
            void Circle(float x, float y, float radius) { p.BeginPath(); p.Arc(At(x, y), radius * s, 0, 360); p.Stroke(); }
            p.strokeColor = tint; p.fillColor = tint; p.lineWidth = Mathf.Max(1.2f, s * 0.08f);
            p.lineCap = LineCap.Round; p.lineJoin = LineJoin.Round;
            switch (kind)
            {
                case GlyphKind.Bus:
                    Lines(true, At(0.2f, 0.1f), At(0.8f, 0.1f), At(0.8f, 0.76f), At(0.2f, 0.76f));
                    Lines(false, At(0.2f, 0.48f), At(0.8f, 0.48f));
                    Lines(false, At(0.3f, 0.76f), At(0.3f, 0.92f));
                    Lines(false, At(0.7f, 0.76f), At(0.7f, 0.92f));
                    break;
                case GlyphKind.Pin:
                    Circle(0.5f, 0.38f, 0.28f);
                    Circle(0.5f, 0.38f, 0.08f);
                    Lines(false, At(0.28f, 0.56f), At(0.5f, 0.94f), At(0.72f, 0.56f));
                    break;
                case GlyphKind.Warning:
                    Lines(true, At(0.5f, 0.1f), At(0.95f, 0.88f), At(0.05f, 0.88f));
                    Lines(false, At(0.5f, 0.38f), At(0.5f, 0.62f));
                    Lines(false, At(0.5f, 0.74f), At(0.5f, 0.76f));
                    break;
                case GlyphKind.Plug:
                    Lines(false, At(0.36f, 0.08f), At(0.36f, 0.32f));
                    Lines(false, At(0.64f, 0.08f), At(0.64f, 0.32f));
                    Lines(true, At(0.22f, 0.32f), At(0.78f, 0.32f), At(0.68f, 0.58f), At(0.32f, 0.58f));
                    Lines(false, At(0.5f, 0.58f), At(0.5f, 0.92f));
                    break;
                case GlyphKind.Document:
                    Lines(true, At(0.24f, 0.1f), At(0.6f, 0.1f), At(0.78f, 0.28f), At(0.78f, 0.9f), At(0.24f, 0.9f));
                    foreach (var y in new[] { 0.46f, 0.6f, 0.74f }) Lines(false, At(0.36f, y), At(0.66f, y));
                    break;
                case GlyphKind.User:
                    Circle(0.5f, 0.32f, 0.18f);
                    Lines(false, At(0.16f, 0.9f), At(0.26f, 0.66f), At(0.74f, 0.66f), At(0.84f, 0.9f));
                    break;
                case GlyphKind.Lock:
                    Lines(true, At(0.22f, 0.46f), At(0.78f, 0.46f), At(0.78f, 0.9f), At(0.22f, 0.9f));
                    Lines(false, At(0.34f, 0.46f), At(0.34f, 0.26f), At(0.5f, 0.1f), At(0.66f, 0.26f), At(0.66f, 0.46f));
                    break;
                case GlyphKind.Eye:
                    Lines(true, At(0.06f, 0.5f), At(0.3f, 0.28f), At(0.7f, 0.28f), At(0.94f, 0.5f), At(0.7f, 0.72f), At(0.3f, 0.72f));
                    Circle(0.5f, 0.5f, 0.12f);
                    break;
                case GlyphKind.Fit:
                    Lines(false, At(0.14f, 0.86f), At(0.86f, 0.14f));
                    Lines(false, At(0.5f, 0.14f), At(0.86f, 0.14f), At(0.86f, 0.5f));
                    Lines(false, At(0.14f, 0.5f), At(0.14f, 0.86f), At(0.5f, 0.86f));
                    break;
                case GlyphKind.Compass:
                    p.BeginPath(); p.MoveTo(At(0.5f, 0.02f)); p.LineTo(At(0.76f, 0.62f)); p.LineTo(At(0.24f, 0.62f)); p.ClosePath(); p.Fill();
                    break;
                case GlyphKind.Info:
                    Circle(0.5f, 0.5f, 0.42f);
                    Lines(false, At(0.5f, 0.46f), At(0.5f, 0.72f));
                    Lines(false, At(0.5f, 0.28f), At(0.5f, 0.3f));
                    break;
                case GlyphKind.LogOut:
                    // An open door frame with an arrow leaving it to the right
                    Lines(false, At(0.5f, 0.14f), At(0.18f, 0.14f), At(0.18f, 0.86f), At(0.5f, 0.86f));
                    Lines(false, At(0.38f, 0.5f), At(0.9f, 0.5f));
                    Lines(false, At(0.74f, 0.34f), At(0.9f, 0.5f), At(0.74f, 0.66f));
                    break;
                case GlyphKind.Power:
                    // A power symbol: a ring open at the top with a line through the gap
                    p.BeginPath(); p.Arc(At(0.5f, 0.54f), 0.34f * s, -55, 235); p.Stroke();
                    Lines(false, At(0.5f, 0.08f), At(0.5f, 0.48f));
                    break;
                case GlyphKind.Refresh:
                    // A circular arrow: a ring open at the top, an arrowhead on its upper-left end pointing clockwise
                    p.BeginPath(); p.Arc(At(0.5f, 0.54f), 0.32f * s, -60, 240); p.Stroke();
                    p.BeginPath(); p.MoveTo(At(0.46f, 0.19f)); p.LineTo(At(0.28f, 0.16f)); p.LineTo(At(0.40f, 0.37f)); p.ClosePath(); p.Fill();
                    break;
                case GlyphKind.Trash:
                    // A bin: its lid and handle, a body narrowing to the bottom, two ribs
                    Lines(false, At(0.16f, 0.24f), At(0.84f, 0.24f));
                    Lines(false, At(0.4f, 0.24f), At(0.4f, 0.12f), At(0.6f, 0.12f), At(0.6f, 0.24f));
                    Lines(true, At(0.24f, 0.24f), At(0.76f, 0.24f), At(0.7f, 0.9f), At(0.3f, 0.9f));
                    Lines(false, At(0.43f, 0.4f), At(0.44f, 0.76f));
                    Lines(false, At(0.57f, 0.4f), At(0.56f, 0.76f));
                    break;
                case GlyphKind.Archive:
                    // A storage box: its lid, the box under it and a handle slot
                    Lines(true, At(0.1f, 0.16f), At(0.9f, 0.16f), At(0.9f, 0.36f), At(0.1f, 0.36f));
                    Lines(false, At(0.16f, 0.36f), At(0.16f, 0.88f), At(0.84f, 0.88f), At(0.84f, 0.36f));
                    Lines(false, At(0.4f, 0.54f), At(0.6f, 0.54f));
                    break;
                case GlyphKind.Clock:
                    Circle(0.5f, 0.5f, 0.4f);
                    Lines(false, At(0.5f, 0.26f), At(0.5f, 0.5f), At(0.68f, 0.62f));
                    break;
                case GlyphKind.Bell:
                case GlyphKind.BellOff:
                    // A bell: its sides rising to a dome, the flared rim, the knob on top and the clapper.
                    // Muted, a slash crosses it.
                    p.BeginPath();
                    p.MoveTo(At(0.18f, 0.74f));
                    p.LineTo(At(0.28f, 0.62f));
                    p.LineTo(At(0.28f, 0.44f));
                    p.Arc(At(0.5f, 0.44f), 0.22f * s, 180, 360);
                    p.LineTo(At(0.72f, 0.62f));
                    p.LineTo(At(0.82f, 0.74f));
                    p.ClosePath();
                    p.Stroke();
                    Lines(false, At(0.5f, 0.12f), At(0.5f, 0.22f));
                    Lines(false, At(0.42f, 0.86f), At(0.58f, 0.86f));
                    if (kind == GlyphKind.BellOff) Lines(false, At(0.12f, 0.1f), At(0.88f, 0.92f));
                    break;
                case GlyphKind.Disk:
                    // A hard drive seen from the front: its case, a divide, and the activity light
                    Lines(true, At(0.1f, 0.28f), At(0.9f, 0.28f), At(0.9f, 0.72f), At(0.1f, 0.72f));
                    Lines(false, At(0.1f, 0.52f), At(0.9f, 0.52f));
                    Circle(0.76f, 0.62f, 0.03f);
                    break;
            }
        }
    }
}
