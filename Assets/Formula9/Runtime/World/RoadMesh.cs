using System.Collections.Generic;
using System.Linq;
using Formula9.Backend;
using UnityEngine;
using UnityEngine.Rendering;

namespace Formula9.World
{
    // Lays roads flat over the map tiles as one mesh of three layers, drawn in this order: a pale
    // kerb, the asphalt, then the white lines. The layers overlap freely where roads meet, so each is
    // painted over the last rather than sorted by depth.
    public static class RoadMesh
    {
        public const int KerbLayer = 0, AsphaltLayer = 1, MarkingLayer = 2, LayerCount = 3;
        // The backend places vehicles in lanes of this width; CityRoadMarkings.shader draws its lines by it
        public const float LaneWidthM = 3.2f;
        public const float ShoulderM = 0.6f, KerbM = 0.55f;
        // Just above the ground, under the routes
        public const float SurfaceY = 0.1f;
        // Lines stop this far short of the widest road at a junction, leaving it plain asphalt
        private const float JunctionClearanceM = 1;
        // A bend sharper than this gets a round corner, because its mitre is cut short
        private const float SharpBendCosine = 0.7f, MaxMitre = 2;
        private const int CapSides = 12;
        private const float MinSegmentM = 1e-3f;

        public static float HalfWidth(CityRoad road) => (road.Oneway ? road.Lanes : road.Lanes * 2) * LaneWidthM / 2 + ShoulderM;

        private static bool IsUnmarkedClass(string roadClass)
        {
            switch ((roadClass ?? string.Empty).ToLowerInvariant())
            {
                case "service":
                case "path":
                case "footway":
                case "pedestrian":
                case "cycleway":
                case "steps":
                case "track":
                case "construction":
                case "platform":
                    return true;

                default:
                    return false;
            }
        }

       public static Mesh Build(IEnumerable<CityRoad> roads)
        {
            var list = roads.Select(road => (Road: road, Points: Line(road))).Where(r => r.Points.Count >= 2).ToList();
            var ends = new Dictionary<long, List<float>>();
            void AddEnd(long node, float halfWidth)
            {
                if (!ends.TryGetValue(node, out var widths)) ends[node] = widths = new List<float>();
                widths.Add(halfWidth);
            }
            foreach (var (road, _) in list) { AddEnd(road.FromNode, HalfWidth(road)); AddEnd(road.ToNode, HalfWidth(road)); }
            // Where only two roads meet, one carries on as the other, so its lines do too
            float Clearance(long node) => ends[node].Count > 2 ? ends[node].Max() + JunctionClearanceM : 0;

            var vertices = new List<Vector3>(); var lines = new List<Vector4>();
            var textureCoords = new List<Vector4>();
            var fadeCoords = new List<Vector4>();
            var layers = Enumerable.Range(0, LayerCount).Select(_ => new List<int>()).ToArray();

            // Draw only the asphalt ribbon. The old circular endpoint/bend Cap()
            // emitted a fan of zero-UV triangles at every sharp vertex. Under the
            // textured road shader those polygons had different colours from the
            // neighbouring road, creating dark star and triangle artifacts.
            void Surface(List<Vector2> points, float halfWidth, List<int> triangles, float height)
            {
                Ribbon(points, halfWidth, null, 0, 0, 0, 0,
                    vertices, lines, triangles, height);
            }
            // Kerbs are two OUTER edge strips, not another full-width road polygon.
            // Full-width kerb underlays used to show through adjacent road junctions
            // as thick grey bars whenever road surfaces failed to coincide exactly.
            foreach (var (road, points) in list)
                KerbEdges(points, HalfWidth(road), vertices, lines, layers[KerbLayer]);

            // TEXCOORD1: road width, texture repeats, signed lane count, marked flag.
            // TEXCOORD2: fade start/end (metres), fade span (metres), unused.
            // Cutoff data is constant for each road and evaluated PER PIXEL, not
            // interpolated from sparse polyline vertices.
            while (textureCoords.Count < vertices.Count) textureCoords.Add(Vector4.zero);
            while (fadeCoords.Count < vertices.Count) fadeCoords.Add(Vector4.zero);
            foreach (var (road, points) in list)
            {
                int first = vertices.Count;
                Surface(points, HalfWidth(road), layers[AsphaltLayer], SurfaceY);
                while (textureCoords.Count < vertices.Count) textureCoords.Add(Vector4.zero);
                while (fadeCoords.Count < vertices.Count) fadeCoords.Add(Vector4.zero);
                var along = Along(points);
                float length = along[along.Count - 1];
                // Never erase an entire short road connecting two junctions.
                float fromCut = Mathf.Min(Clearance(road.FromNode), length * .25f);
                float toCut = Mathf.Min(Clearance(road.ToNode), length * .25f);
                float fadeSpan = Mathf.Min(1.2f, Mathf.Max(.10f, length * .12f));
                bool marked = !IsUnmarkedClass(road.Class);
                // Our backend's two-way Lanes represents lanes per direction.
                int totalLanes = road.Oneway ? road.Lanes : road.Lanes * 2;
                totalLanes = Mathf.Max(1, totalLanes);
                for (int i = 0; i < points.Count; i++)
                {
                    float v = along[i] / 24f; // Source texture's dash cycle
                    float signedLanes = road.Oneway ? totalLanes : -totalLanes;
                    var paintData = new Vector4(fromCut, length - toCut, fadeSpan,
                        toCut > 0f ? 1f : 0f);
                    textureCoords[first + i * 2] = new Vector4(1f, v, signedLanes, marked ? 1f : 0f);
                    textureCoords[first + i * 2 + 1] = new Vector4(0f, v, signedLanes, marked ? 1f : 0f);
                    fadeCoords[first + i * 2] = paintData;
                    fadeCoords[first + i * 2 + 1] = paintData;
                }
                // Every asphalt vertex now has valid lane UVs. There are no
                // cap/fan polygons to create triangles with zero lane UVs.
            }
            // The deleted CityRoadMarkings shader no longer consumes the legacy
            // submesh. Do not generate invisible geometry merely to delete it later.
            // Lane paint is sampled by CityRoadTexture on the asphalt submesh.

            var mesh = new Mesh { name = "City roads", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(vertices); mesh.SetUVs(0, lines); mesh.SetUVs(1, textureCoords); mesh.SetUVs(2, fadeCoords);
            mesh.subMeshCount = LayerCount;
            for (var i = 0; i < LayerCount; i++) mesh.SetTriangles(layers[i], i);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            return mesh;
        }

        
        // The road's points without repeats: canonical (x, y) is Unity (x, z)
        private static List<Vector2> Line(CityRoad road)
        {
            var points = new List<Vector2>();
            foreach (var (x, y) in road.Points)
            {
                var point = new Vector2((float)x, (float)y);
                if (points.Count == 0 || (point - points[points.Count - 1]).magnitude > MinSegmentM) points.Add(point);
            }
            return points;
        }

        private static List<float> Along(List<Vector2> points)
        {
            var along = new List<float> { 0 };
            for (var i = 1; i < points.Count; i++) along.Add(along[i - 1] + (points[i] - points[i - 1]).magnitude);
            return along;
        }

        // The part of a line between two distances along it
        private static (List<Vector2> Points, List<float> Along) Slice(List<Vector2> points, List<float> along, float from, float to)
        {
            Vector2 At(float distance)
            {
                var i = 1;
                while (i < points.Count - 1 && along[i] < distance) i++;
                return Vector2.Lerp(points[i - 1], points[i], Mathf.InverseLerp(along[i - 1], along[i], distance));
            }
            var cut = new List<Vector2> { At(from) }; var cutAlong = new List<float> { from };
            for (var i = 0; i < points.Count; i++)
                if (along[i] > from + MinSegmentM && along[i] < to - MinSegmentM) { cut.Add(points[i]); cutAlong.Add(along[i]); }
            cut.Add(At(to)); cutAlong.Add(to);
            return (cut, cutAlong);
        }

        private static bool IsSharp(Vector2 before, Vector2 at, Vector2 after) =>
            Vector2.Dot((at - before).normalized, (after - at).normalized) < SharpBendCosine;

        private static Vector2 LeftOf(Vector2 direction) => new Vector2(-direction.y, direction.x);

        // A strip along a line, mitred at its bends. Each vertex tells the marking shader (lane position
        // across the road, metres along it, lanes, 1 if two-way).
        private static void Ribbon(List<Vector2> points, float halfWidth, List<float> along, float middleLane, float halfLanes, int lanes, int twoWay, List<Vector3> vertices, List<Vector4> lines, List<int> triangles, float height)
        {
            for (var i = 0; i < points.Count; i++)
            {
                var into = (points[Mathf.Max(i, 1)] - points[Mathf.Max(i, 1) - 1]).normalized;
                var outOf = (points[Mathf.Min(i + 1, points.Count - 1)] - points[Mathf.Min(i + 1, points.Count - 1) - 1]).normalized;
                var left = LeftOf(into + outOf);
                // A line that doubles straight back has no mitre
                left = left.sqrMagnitude < 1e-6f ? LeftOf(outOf) : left.normalized;
                var stretch = Mathf.Min(1 / Mathf.Max(Vector2.Dot(left, LeftOf(outOf)), 1e-3f), MaxMitre);
                var offset = left * (halfWidth * stretch);
                var start = vertices.Count;
                vertices.Add(Ground(points[i] + offset, height)); vertices.Add(Ground(points[i] - offset, height));
                var distance = along == null ? 0 : along[i];
                lines.Add(new Vector4(middleLane + halfLanes, distance, lanes, twoWay));
                lines.Add(new Vector4(middleLane - halfLanes, distance, lanes, twoWay));
                // Clockwise from above, so it faces up
                if (i > 0) triangles.AddRange(new[] { start - 2, start, start + 1, start - 2, start + 1, start - 1 });
            }
        }

        // Create a pair of kerb-only edge bands instead of a complete second
        // carriageway. Keep the original public RoadMesh API and all 3 submeshes
        // so existing EditMode tests and consumers remain compatible.
        private static void KerbEdges(List<Vector2> points, float asphaltHalfWidth,
            List<Vector3> vertices, List<Vector4> lines, List<int> triangles)
        {
            if (points.Count < 2) return;
            var normals = new Vector2[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                var previous = (points[i] - points[Mathf.Max(0, i - 1)]).normalized;
                var next = (points[Mathf.Min(i + 1, points.Count - 1)] - points[i]).normalized;
                if (i == 0) previous = next;
                if (i == points.Count - 1) next = previous;
                var leftPrev = LeftOf(previous);
                var leftNext = LeftOf(next);
                var miter = leftPrev + leftNext;
                miter = miter.sqrMagnitude < 1e-6f ? leftNext : miter.normalized;
                var stretch = Mathf.Min(1f / Mathf.Max(Vector2.Dot(miter, leftNext), 0.001f), MaxMitre);
                normals[i] = miter * stretch;
            }

            void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
            {
                int index = vertices.Count;
                vertices.Add(Ground(a, SurfaceY));
                vertices.Add(Ground(b, SurfaceY));
                vertices.Add(Ground(c, SurfaceY));
                vertices.Add(Ground(d, SurfaceY));
                for (int k = 0; k < 4; k++) lines.Add(Vector4.zero);
                triangles.AddRange(new[] { index, index + 1, index + 2,
                    index, index + 2, index + 3 });
            }

            for (int i = 1; i < points.Count; i++)
            {
                var a = points[i - 1]; var b = points[i];
                if ((b - a).sqrMagnitude < MinSegmentM * MinSegmentM) continue;
                var na = normals[i - 1]; var nb = normals[i];
                float inner = asphaltHalfWidth;
                float outer = asphaltHalfWidth + KerbM;
                Quad(a + na * inner, a + na * outer,
                     b + nb * outer, b + nb * inner);
                Quad(a - na * inner, b - nb * inner,
                     b - nb * outer, a - na * outer);
            }
        }

        private static void Cap(Vector2 center, float radius, List<Vector3> vertices, List<Vector4> lines, List<int> triangles, float height)
        {
            var start = vertices.Count;
            vertices.Add(Ground(center, height)); lines.Add(Vector4.zero);
            for (var i = 0; i < CapSides; i++)
            {
                var angle = -2 * Mathf.PI * i / CapSides;
                vertices.Add(Ground(center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius, height)); lines.Add(Vector4.zero);
                triangles.AddRange(new[] { start, start + 1 + i, start + 1 + (i + 1) % CapSides });
            }
        }

        private static Vector3 Ground(Vector2 point, float height) => new Vector3(point.x, height, point.y);
    }
}
