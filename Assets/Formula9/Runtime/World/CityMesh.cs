using System.Collections.Generic;
using System.Linq;
using Formula9.Backend;
using UnityEngine;
using UnityEngine.Rendering;

namespace Formula9.World
{
    // Extrudes building outlines into one mesh: walls straight up from the outline, a flat roof on top.
    public static class CityMesh
    {
        public static Mesh Build(IEnumerable<CityBuilding> buildings)
        {
            var vertices = new List<Vector3>(); var colors = new List<Color>(); var triangles = new List<int>();
            foreach (var building in buildings)
            {
                var color = ColorFor(building);
                foreach (var ring in building.Rings)
                    Extrude(ring.Select(p => new Vector2((float)p.X, (float)p.Y)).ToList(), (float)building.HeightM, color, vertices, colors, triangles);
            }
            var mesh = new Mesh { name = "City buildings", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(vertices); mesh.SetColors(colors); mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            return mesh;
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
        private static void Extrude(List<Vector2> outline, float height, Color color, List<Vector3> vertices, List<Color> colors, List<int> triangles)
        {
            var points = new List<Vector2>(outline);
            if (points.Count > 1 && points[0] == points[points.Count - 1]) points.RemoveAt(points.Count - 1);
            if (points.Count < 3 || height <= 0) return;
            if (SignedArea(points) < 0) points.Reverse();
            for (var i = 0; i < points.Count; i++)
            {
                var a = points[i]; var b = points[(i + 1) % points.Count];
                var start = vertices.Count;
                vertices.Add(new Vector3(a.x, 0, a.y)); vertices.Add(new Vector3(a.x, height, a.y));
                vertices.Add(new Vector3(b.x, height, b.y)); vertices.Add(new Vector3(b.x, 0, b.y));
                // Walls a little darker low down, so buildings read as solid
                colors.Add(color * 0.8f); colors.Add(color); colors.Add(color); colors.Add(color * 0.8f);
                triangles.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
            }
            var roof = vertices.Count;
            foreach (var point in points) { vertices.Add(new Vector3(point.x, height, point.y)); colors.Add(color * 1.05f); }
            foreach (var triangle in Triangulate(points)) triangles.Add(roof + triangle);
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
