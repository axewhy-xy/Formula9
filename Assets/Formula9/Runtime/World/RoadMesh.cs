using System.Collections.Generic;
using System.Linq;
using Formula9.Backend;
using UnityEngine;
using UnityEngine.Rendering;

namespace Formula9.World
{
    // Flat road geometry at SurfaceY, including narrow outer kerb strips. The CityView
    // renderer draws kerbs before asphalt and uses stencil masking for LTA paint.
    // The third submesh is retained (empty) for consumers expecting LayerCount == 3.
    public static class RoadMesh
    {
        public const int KerbLayer = 0, AsphaltLayer = 1, MarkingLayer = 2, LayerCount = 3;
        // The backend places vehicles in lanes of this width.
        public const float LaneWidthM = 3.2f;
        public const float ShoulderM = 0.6f, KerbM = 0.55f;
        // Just above the ground, under the routes
        public const float SurfaceY = 0.1f;
        // All road submeshes share SurfaceY, preserving stable mesh bounds.
        // Shaders use polygon offset; kerbs never write depth.
        // Lines stop this far short of the widest road at a junction, leaving it plain asphalt
        private const float JunctionClearanceM = 1;
        // Paths and service access roads have no painted lane boundaries.
        private static bool IsUnmarkedClass(string roadClass)
        {
            switch ((roadClass ?? string.Empty).ToLowerInvariant())
            {
                case "service": case "path": case "footway": case "pedestrian":
                case "cycleway": case "steps": case "track": case "construction":
                case "platform": return true;
                default: return false;
            }
        }
        // A bend sharper than this gets a round corner, because its mitre is cut short
        private const float MaxMitre = 2;
        private const float MinSegmentM = 1e-3f;

        public static float HalfWidth(CityRoad road) => (road.Oneway ? road.Lanes : road.Lanes * 2) * LaneWidthM / 2 + ShoulderM;

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
            // Draw asphalt ribbons, then attach correctly textured rounded end
            // caps. The old untextured Cap() fan caused dark triangles.
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

                // Rounded END caps join neighbouring road ribbons at intersections.
                // Their UVs are projected from this road's own texture basis, so
                // they don't reproduce the former zero-UV triangle/star artefacts.
                var laneCount = road.Oneway ? totalLanes : -totalLanes;
                var paintDataCap = new Vector4(fromCut, length - toCut, fadeSpan,
                    toCut > 0f ? 1f : 0f);
                EndpointCap(points[0], points[1] - points[0], true,
                    HalfWidth(road), 0f, laneCount, marked, paintDataCap,
                    vertices, lines, textureCoords, fadeCoords, layers[AsphaltLayer]);
                int last = points.Count - 1;
                EndpointCap(points[last], points[last] - points[last - 1], false,
                    HalfWidth(road), length, laneCount, marked, paintDataCap,
                    vertices, lines, textureCoords, fadeCoords, layers[AsphaltLayer]);
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

        private static Vector2 LeftOf(Vector2 direction) => new Vector2(-direction.y, direction.x);

        // A strip along a road, mitred at its bends. UV0 is retained for API compatibility.
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

        // Fill only the rounded half of a road endpoint, not a full overlaid disc.
        // Unlike the old Cap() mesh, all vertices have valid texture and fade UVs.
        // This keeps the asphalt continuous at junctions without zero-UV wedges.
        private static void EndpointCap(Vector2 center, Vector2 direction, bool atStart,
            float radius, float alongM, float signedLanes, bool marked,
            Vector4 fadeData, List<Vector3> vertices, List<Vector4> lines,
            List<Vector4> textureCoords, List<Vector4> fadeCoords, List<int> triangles)
        {
            if (direction.sqrMagnitude < MinSegmentM * MinSegmentM || radius <= 0f)
                return;
            const int arcSteps = 12;
            var forward = direction.normalized;
            var outside = atStart ? -forward : forward;
            var left = LeftOf(forward);
            int first = vertices.Count;

            void Add(Vector2 point)
            {
                var delta = point - center;
                float across = Mathf.Clamp01(.5f + Vector2.Dot(delta, left) / (2f * radius));
                float localAlong = (alongM + Vector2.Dot(delta, forward)) / 24f;
                vertices.Add(Ground(point, SurfaceY));
                lines.Add(Vector4.zero);
                textureCoords.Add(new Vector4(across, localAlong, signedLanes,
                    marked ? 1f : 0f));
                fadeCoords.Add(fadeData);
            }

            Add(center);
            // Arc extends OUTSIDE the endpoint; the ordinary ribbon covers
            // the inner half. Both sides meet at the same road-edge vertices.
            for (int i = 0; i <= arcSteps; ++i)
            {
                float a = -Mathf.PI * .5f + Mathf.PI * i / arcSteps;
                Add(center + radius * (outside * Mathf.Cos(a) + left * Mathf.Sin(a)));
            }
            for (int i = 0; i < arcSteps; ++i)
            {
                var a = vertices[first + i + 1];
                var b = vertices[first + i + 2];
                var cross2 = (a.x - center.x) * (b.z - center.y) -
                    (a.z - center.y) * (b.x - center.x);
                // XZ triangle winding is reversed relative to XY. Positive
                // Unity Y normal requires negative signed XY area.
                if (cross2 <= 0f)
                {
                    triangles.Add(first);
                    triangles.Add(first + i + 1);
                    triangles.Add(first + i + 2);
                }
                else
                {
                    triangles.Add(first);
                    triangles.Add(first + i + 2);
                    triangles.Add(first + i + 1);
                }
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

        private static Vector3 Ground(Vector2 point, float height) => new Vector3(point.x, height, point.y);
    }
}
