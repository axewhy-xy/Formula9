using System.Collections.Generic;
using System.Linq;
using Formula9.Backend;
using UnityEngine;
using UnityEngine.Rendering;

namespace Formula9.World
{
    // A strip of ground no building may stand on: a rectangle, anticlockwise seen from above.
    public sealed class Corridor
    {
        public readonly Vector2[] Corners;
        public readonly Rect Box;

        public Corridor(Vector2[] corners)
        {
            Corners = corners;
            Box = CityMesh.BoxAround(corners);
        }
    }

    // Extrudes building outlines into one mesh: walls straight up from the outline, a flat roof on top.
    // OSM outlines ignore what passes under a building, so corridors are cut out from ground to roof.
    public static class CityMesh
    {
        // Pieces smaller than this only come from rounding, and are not drawn
        private const float MinWallM = 1e-3f, MinRoofM2 = 1e-4f;
        // A wall exactly on a corridor's edge stays, so corridors that share an edge keep their wall
        private const float EdgeToleranceM = 1e-3f;
        // Less of an outline than this left over is only thin strips, like the sides of a canopy over the road
        private const float MinLeftShare = 0.2f;

        public static Mesh Build(IEnumerable<CityBuilding> buildings, IReadOnlyList<Corridor> corridors = null)
        {
            var mesh = new Mesh { name = "City buildings" };
            Fill(mesh, buildings, corridors);
            return mesh;
        }

        // Rebuilds a mesh in place, for when the corridors change
        public static void Fill(Mesh mesh, IEnumerable<CityBuilding> buildings, IReadOnlyList<Corridor> corridors = null)
        {
            var vertices = new List<Vector3>(); var colors = new List<Color>(); var triangles = new List<int>();
            foreach (var building in buildings)
            {
                var color = ColorFor(building);
                foreach (var ring in building.Rings)
                    Extrude(ring.Select(p => new Vector2((float)p.X, (float)p.Y)).ToList(), (float)building.HeightM, color, corridors, vertices, colors, triangles);
            }
            mesh.Clear();
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(vertices); mesh.SetColors(colors); mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
        }

        // One rectangle per segment of a path. Each runs half its width past its ends, so the
        // rectangles still overlap on the outside of a bend.
        public static List<Corridor> Corridors(IEnumerable<IReadOnlyList<Vector2>> paths, float halfWidth)
        {
            var corridors = new List<Corridor>();
            foreach (var path in paths)
                for (var i = 0; i + 1 < path.Count; i++)
                {
                    var along = path[i + 1] - path[i];
                    if (along.magnitude < MinWallM) continue;
                    along = along.normalized * halfWidth;
                    var left = new Vector2(-along.y, along.x);
                    Vector2 start = path[i] - along, end = path[i + 1] + along;
                    corridors.Add(new Corridor(new[] { start - left, end - left, end + left, start + left }));
                }
            return corridors;
        }

        public static Rect BoxAround(IReadOnlyList<Vector2> points)
        {
            Vector2 min = points[0], max = points[0];
            foreach (var point in points) { min = Vector2.Min(min, point); max = Vector2.Max(max, point); }
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        private static Color ColorFor(CityBuilding building)
        {
            Color baseColor;
            switch (building.Type)
            {
                case "house": case "residential": case "apartments": case "terrace": case "detached": baseColor = new Color(0.87f, 0.84f, 0.78f); break;
                case "retail": case "commercial": baseColor = new Color(0.78f, 0.82f, 0.86f); break;
                case "office": baseColor = new Color(0.70f, 0.78f, 0.84f); break;
                case "industrial": case "warehouse": baseColor = new Color(0.72f, 0.72f, 0.72f); break;
                default: baseColor = new Color(0.82f, 0.81f, 0.78f); break;
            }
            // A little variation by building, repeatable from its ID, so neighbours do not merge
            var shade = 0.92f + (StableHash(building.Id) % 100) * 0.0016f;
            return new Color(baseColor.r * shade, baseColor.g * shade, baseColor.b * shade);
        }

        private static uint StableHash(string text)
        {
            var hash = 2166136261u;
            foreach (var c in text) hash = (hash ^ c) * 16777619u;
            return hash;
        }

        // Canonical (x, y) is Unity (x, z). Front faces are clockwise seen from outside.
        private static void Extrude(List<Vector2> outline, float height, Color color, IReadOnlyList<Corridor> corridors, List<Vector3> vertices, List<Color> colors, List<int> triangles)
        {
            var points = new List<Vector2>(outline);
            if (points.Count > 1 && points[0] == points[points.Count - 1]) points.RemoveAt(points.Count - 1);
            if (points.Count < 3 || height <= 0) return;
            if (SignedArea(points) < 0) points.Reverse();
            var box = BoxAround(points);
            var cuts = corridors == null ? new List<Corridor>() : corridors.Where(c => c.Box.Overlaps(box)).ToList();
            var whole = Triangulate(points);
            var roofPieces = new List<List<Vector2>>();
            for (var i = 0; cuts.Count > 0 && i < whole.Count; i += 3)
            {
                var pieces = new List<List<Vector2>> { new List<Vector2> { points[whole[i]], points[whole[i + 2]], points[whole[i + 1]] } };
                foreach (var cut in cuts) pieces = pieces.SelectMany(piece => Subtract(piece, cut.Corners)).ToList();
                roofPieces.AddRange(pieces);
            }
            if (cuts.Count > 0 && roofPieces.Sum(piece => Mathf.Abs(SignedArea(piece))) < MinLeftShare * SignedArea(points)) return;
            void Wall(Vector2 a, Vector2 b)
            {
                var start = vertices.Count;
                vertices.Add(new Vector3(a.x, 0, a.y)); vertices.Add(new Vector3(a.x, height, a.y));
                vertices.Add(new Vector3(b.x, height, b.y)); vertices.Add(new Vector3(b.x, 0, b.y));
                // Walls a little darker low down, so buildings read as solid
                colors.Add(color * 0.8f); colors.Add(color); colors.Add(color); colors.Add(color * 0.8f);
                triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
            }
            for (var i = 0; i < points.Count; i++)
                foreach (var (p, q) in Outside(points[i], points[(i + 1) % points.Count], cuts)) Wall(p, q);
            // The sides of a cut are new walls, facing into the corridor: its inside is left of each edge
            foreach (var cut in cuts)
            {
                var others = cuts.Where(c => c != cut).ToList();
                for (var i = 0; i < cut.Corners.Length; i++)
                    foreach (var (enter, leave) in InsideOutline(cut.Corners[i], cut.Corners[(i + 1) % cut.Corners.Length], points))
                        foreach (var (p, q) in Outside(enter, leave, others)) Wall(q, p);
            }
            if (cuts.Count == 0)
            {
                var roof = vertices.Count;
                foreach (var point in points) { vertices.Add(new Vector3(point.x, height, point.y)); colors.Add(color * 1.05f); }
                foreach (var triangle in whole) triangles.Add(roof + triangle);
                return;
            }
            foreach (var piece in roofPieces)
            {
                if (SignedArea(piece) < 0) piece.Reverse();
                var roof = vertices.Count;
                foreach (var point in piece) { vertices.Add(new Vector3(point.x, height, point.y)); colors.Add(color * 1.05f); }
                for (var j = 1; j + 1 < piece.Count; j++) triangles.AddRange(new[] { roof, roof + j + 1, roof + j });
            }
        }

        // The parts of a line that no corridor covers
        private static List<(Vector2 From, Vector2 To)> Outside(Vector2 a, Vector2 b, List<Corridor> cuts)
        {
            var parts = new List<(float From, float To)> { (0, 1) };
            foreach (var cut in cuts)
            {
                if (!Crosses(a, b, cut.Corners, out var enter, out var leave)) continue;
                parts = parts.SelectMany(p => new[] { (p.From, Mathf.Min(p.To, enter)), (Mathf.Max(p.From, leave), p.To) }).ToList();
            }
            return ToSegments(a, b, parts);
        }

        // Where a line runs through a corridor, as fractions of the line
        private static bool Crosses(Vector2 a, Vector2 b, Vector2[] corners, out float enter, out float leave)
        {
            enter = 0; leave = 1;
            for (var i = 0; i < corners.Length; i++)
            {
                var inward = Inward(corners[i], corners[(i + 1) % corners.Length]);
                var fromA = Vector2.Dot(a - corners[i], inward) - EdgeToleranceM;
                var fromB = Vector2.Dot(b - corners[i], inward) - EdgeToleranceM;
                if (fromA <= 0 && fromB <= 0) return false;
                if (fromA < 0) enter = Mathf.Max(enter, fromA / (fromA - fromB));
                else if (fromB < 0) leave = Mathf.Min(leave, fromA / (fromA - fromB));
            }
            return enter < leave;
        }

        // The parts of a line inside an outline
        private static List<(Vector2 From, Vector2 To)> InsideOutline(Vector2 a, Vector2 b, List<Vector2> outline)
        {
            var stops = new List<float> { 0, 1 };
            var along = b - a;
            for (var i = 0; i < outline.Count; i++)
            {
                Vector2 c = outline[i], side = outline[(i + 1) % outline.Count] - c;
                var denominator = along.x * side.y - along.y * side.x;
                if (Mathf.Approximately(denominator, 0)) continue;
                var onLine = ((c.x - a.x) * side.y - (c.y - a.y) * side.x) / denominator;
                var onSide = ((c.x - a.x) * along.y - (c.y - a.y) * along.x) / denominator;
                if (onLine > 0 && onLine < 1 && onSide >= 0 && onSide <= 1) stops.Add(onLine);
            }
            stops.Sort();
            var parts = new List<(float From, float To)>();
            for (var i = 0; i + 1 < stops.Count; i++)
                if (Contains(outline, a + along * ((stops[i] + stops[i + 1]) / 2))) parts.Add((stops[i], stops[i + 1]));
            return ToSegments(a, b, parts);
        }

        private static List<(Vector2 From, Vector2 To)> ToSegments(Vector2 a, Vector2 b, List<(float From, float To)> parts) =>
            parts.Where(p => p.To > p.From).Select(p => (From: Vector2.LerpUnclamped(a, b, p.From), To: Vector2.LerpUnclamped(a, b, p.To)))
                .Where(s => (s.To - s.From).magnitude > MinWallM).ToList();

        private static bool Contains(List<Vector2> outline, Vector2 point)
        {
            var inside = false;
            for (var i = 0; i < outline.Count; i++)
            {
                Vector2 a = outline[i], b = outline[(i + 1) % outline.Count];
                if ((a.y > point.y) != (b.y > point.y) && point.x < a.x + (b.x - a.x) * (point.y - a.y) / (b.y - a.y)) inside = !inside;
            }
            return inside;
        }

        // A convex piece of roof minus a corridor, as convex pieces: what lies beyond each edge in turn
        private static List<List<Vector2>> Subtract(List<Vector2> piece, Vector2[] corners)
        {
            var kept = new List<List<Vector2>>();
            var rest = piece;
            for (var i = 0; i < corners.Length && rest.Count >= 3; i++)
            {
                var inward = Inward(corners[i], corners[(i + 1) % corners.Length]);
                var inside = new List<Vector2>(); var beyond = new List<Vector2>();
                for (var j = 0; j < rest.Count; j++)
                {
                    Vector2 a = rest[j], b = rest[(j + 1) % rest.Count];
                    float fromA = Vector2.Dot(a - corners[i], inward), fromB = Vector2.Dot(b - corners[i], inward);
                    if (fromA >= 0) inside.Add(a);
                    if (fromA <= 0) beyond.Add(a);
                    if (fromA * fromB >= 0) continue;
                    var crossing = Vector2.Lerp(a, b, fromA / (fromA - fromB));
                    inside.Add(crossing); beyond.Add(crossing);
                }
                if (beyond.Count >= 3 && Mathf.Abs(SignedArea(beyond)) > MinRoofM2) kept.Add(beyond);
                rest = inside;
            }
            // It never reached the corridor, so leave it whole rather than in needless pieces
            if (rest.Count < 3 || Mathf.Abs(SignedArea(rest)) <= MinRoofM2) return new List<List<Vector2>> { piece };
            return kept;
        }

        private static Vector2 Inward(Vector2 from, Vector2 to)
        {
            var along = (to - from).normalized;
            return new Vector2(-along.y, along.x);
        }

        public static float SignedArea(IReadOnlyList<Vector2> points)
        {
            var area = 0f;
            for (var i = 0; i < points.Count; i++)
            {
                var a = points[i]; var b = points[(i + 1) % points.Count];
                area += a.x * b.y - b.x * a.y;
            }
            return area / 2;
        }

        // Ear clipping for an anticlockwise outline; triangle indices come out clockwise, for a roof
        // seen from above. Real outlines can touch themselves, so anything left over is fanned.
        public static List<int> Triangulate(IReadOnlyList<Vector2> points)
        {
            var result = new List<int>();
            var remaining = Enumerable.Range(0, points.Count).ToList();
            var guard = points.Count * points.Count;
            var at = 0;
            while (remaining.Count > 3 && guard-- > 0)
            {
                var previous = remaining[(at + remaining.Count - 1) % remaining.Count];
                var current = remaining[at % remaining.Count];
                var next = remaining[(at + 1) % remaining.Count];
                if (IsEar(points, remaining, previous, current, next))
                {
                    result.AddRange(new[] { previous, next, current });
                    remaining.RemoveAt(at % remaining.Count);
                    at = 0;
                }
                else at = (at + 1) % remaining.Count;
            }
            for (var i = 1; i + 1 < remaining.Count; i++) result.AddRange(new[] { remaining[0], remaining[i + 1], remaining[i] });
            return result;
        }

        private static bool IsEar(IReadOnlyList<Vector2> points, List<int> remaining, int previous, int current, int next)
        {
            Vector2 a = points[previous], b = points[current], c = points[next];
            if ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x) <= 0) return false;
            foreach (var index in remaining)
            {
                if (index == previous || index == current || index == next) continue;
                if (Inside(points[index], a, b, c)) return false;
            }
            return true;
        }

        private static bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float Side(Vector2 u, Vector2 v) => (v.x - u.x) * (p.y - u.y) - (v.y - u.y) * (p.x - u.x);
            return Side(a, b) >= 0 && Side(b, c) >= 0 && Side(c, a) >= 0;
        }
    }
}
