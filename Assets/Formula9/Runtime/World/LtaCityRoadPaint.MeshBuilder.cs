using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Formula9.World
{
    public sealed partial class LtaCityRoadPaint
    {
        // All drawing methods emit to the same mesh, with one submesh per colour.
        // Inputs are in the asphalt MeshFilter's local XZ coordinates.
        public sealed partial class PaintMeshBuilder
        {
            private const float MinArea = 1e-7f;
            private readonly List<Vector3> vertices = new List<Vector3>();
            private readonly List<int>[] indices = { new List<int>(), new List<int>() };
            private const float CellSizeM = 12f;
            private readonly List<RoadTriangle> road = new List<RoadTriangle>();
            private readonly Dictionary<Vector2Int, List<int>> triangleCells =
                new Dictionary<Vector2Int, List<int>>();
            private readonly List<(Vector2 A, Vector2 B)> laneEdges = new List<(Vector2, Vector2)>();
            private readonly List<(Vector2[] Outline, Rect Bounds)> crossingAreas =
                new List<(Vector2[], Rect)>();
            private readonly bool useCoverage;
            private readonly List<Vector4> edgeDistances = new List<Vector4>();
            private readonly float height;
            public Rect Bounds { get; }

            internal bool DrawArrowsAsSymbols { get; set; }
            internal bool CompactPaintEnvelope { get; set; }
            internal bool HasPaint => indices[0].Count > 0 || indices[1].Count > 0;

            internal PaintMeshBuilder(Vector3[] points, List<int> triangles, Vector2 origin, float halfTile, bool useCoverage)
            {
                this.useCoverage = useCoverage;
                height = RoadMesh.SurfaceY + 0.015f;
                Bounds = Rect.MinMaxRect(-halfTile, -halfTile, halfTile, halfTile);
                var edgeCounts = new Dictionary<(int A, int B), int>();
                for (int i = 0; i < triangles.Count; i += 3)
                {
                    Vector2 a = (Xz(points[triangles[i]]) - origin), b = (Xz(points[triangles[i + 1]]) - origin);
                    Vector2 c = (Xz(points[triangles[i + 2]]) - origin);
                    float turn = Cross(b - a, c - a);
                    if (Mathf.Abs(turn) <= MinArea)
                        continue;
                    var cover = turn > 0 ? new RoadTriangle(a, b, c) : new RoadTriangle(a, c, b);
                    foreach (var cell in Cells(cover.Bounds))
                    {
                        if (!triangleCells.TryGetValue(cell, out List<int> indices))
                        {
                            indices = new List<int>();
                            triangleCells.Add(cell, indices);
                        }
                        indices.Add(road.Count);
                    }
                    road.Add(cover);
                    CountEdge(triangles[i], triangles[i + 1]);
                    CountEdge(triangles[i + 1], triangles[i + 2]);
                    CountEdge(triangles[i + 2], triangles[i]);
                }
                foreach (var edge in edgeCounts)
                    if (edge.Value == 1)
                        laneEdges.Add((Xz(points[edge.Key.A]) - origin, Xz(points[edge.Key.B]) - origin));

                void CountEdge(int a, int b)
                {
                    var key = a < b ? (a, b) : (b, a);
                    edgeCounts.TryGetValue(key, out int count);
                    edgeCounts[key] = count + 1;
                }
            }

            private static IEnumerable<Vector2Int> Cells(Rect bounds)
            {
                int west = Mathf.FloorToInt(bounds.xMin / CellSizeM);
                int east = Mathf.FloorToInt(bounds.xMax / CellSizeM);
                int south = Mathf.FloorToInt(bounds.yMin / CellSizeM);
                int north = Mathf.FloorToInt(bounds.yMax / CellSizeM);
                for (int x = west; x <= east; x++)
                    for (int z = south; z <= north; z++)
                        yield return new Vector2Int(x, z);
            }

            private IEnumerable<RoadTriangle> NearbyTriangles(Rect bounds)
            {
                var visited = new HashSet<int>();
                foreach (var cell in Cells(bounds))
                    if (triangleCells.TryGetValue(cell, out List<int> indices))
                        foreach (int index in indices)
                            if (visited.Add(index))
                                yield return road[index];
            }

            public void AddLaneBoundary(IReadOnlyList<Vector2> path)
            {
                for (int i = 1; i < path.Count; i++)
                    laneEdges.Add((path[i - 1], path[i]));
            }

            public bool IntersectsBounds(IReadOnlyList<Vector2> path, float margin)
            {
                Rect bounds = BoundsOf(path);
                if (useCoverage) margin += PaintMarginM;
                bounds.xMin -= margin;
                bounds.yMin -= margin;
                bounds.xMax += margin;
                bounds.yMax += margin;
                return bounds.Overlaps(Bounds);
            }

            public void ReserveCrossing(Vector2[] outline, float margin)
            {
                if (outline.Length != 4 || Area(outline) <= MinArea)
                    return;
                if (Cross(outline[1] - outline[0], outline[2] - outline[1]) < 0f)
                    System.Array.Reverse(outline);

                var expanded = new Vector2[outline.Length];
                for (int i = 0; i < outline.Length; i++)
                {
                    Vector2 incoming = -Normal(outline[i] - outline[(i + 3) % 4]);
                    Vector2 outgoing = -Normal(outline[(i + 1) % 4] - outline[i]);
                    float scale = Mathf.Max(0.5f, 1f + Vector2.Dot(incoming, outgoing));
                    expanded[i] = outline[i] + (incoming + outgoing) * (margin / scale);
                }
                crossingAreas.Add((expanded, BoundsOf(expanded)));
            }

            // Keep the surveyed boundary as four inward-facing half-planes. The shader can
            // then filter subpixel marks, including arrowheads, after CPU clipping to the road.
            private struct PaintEdges
            {
                internal Vector4 X, Z, Constant;
                internal Vector4 At(Vector2 p) => X * p.x + Z * p.y + Constant;
            }

            private const float PaintMarginM = 3.2f;

            private Vector2[] CoverageEnvelope(IReadOnlyList<Vector2> shape, out PaintEdges edges)
            {
                edges = default;
                float winding = 0f;
                var axis = Vector2.zero;
                for (int i = 0; i < shape.Count; i++)
                {
                    var delta = shape[(i + 1) % shape.Count] - shape[i];
                    winding += Cross(shape[i] - shape[0], delta);
                    if (delta.sqrMagnitude > axis.sqrMagnitude) axis = delta;
                }
                if (Mathf.Abs(winding) < MinArea) return null;
                axis.Normalize();
                var across = new Vector2(-axis.y, axis.x);
                var low = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
                var high = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
                for (int i = 0; i < 4; i++)
                {
                    int j = i % shape.Count;
                    var point = shape[j];
                    var delta = shape[(j + 1) % shape.Count] - point;
                    var inward = Normal(delta) * Mathf.Sign(winding);
                    edges.X[i] = inward.x;
                    edges.Z[i] = inward.y;
                    edges.Constant[i] = delta.sqrMagnitude > 1e-12f ? -Vector2.Dot(point, inward) : 1e4f;
                    var uv = new Vector2(Vector2.Dot(point, axis), Vector2.Dot(point, across));
                    low = Vector2.Min(low, uv);
                    high = Vector2.Max(high, uv);
                }
                float margin = CompactPaintEnvelope ? .2f : PaintMarginM;
                low -= Vector2.one * margin;
                high += Vector2.one * margin;
                return new[] { axis * low.x + across * low.y, axis * high.x + across * low.y,
                    axis * high.x + across * high.y, axis * low.x + across * high.y };
            }

            public void DrawConvex(IReadOnlyList<Vector2> shape, PaintColour colour, bool avoidCrossings = false)
            {
                if (shape.Count < 3 || shape.Count > 4) return;
                PaintEdges edges = default;
                if (useCoverage)
                {
                    shape = CoverageEnvelope(shape, out edges);
                    if (shape == null) return;
                }
                if (avoidCrossings)
                    DrawOutsideCrossings(shape, colour, 0, edges);
                else EmitOnRoad(shape, colour, edges);
            }

            private void EmitOnRoad(IReadOnlyList<Vector2> shape, PaintColour colour, PaintEdges edges)
            {
                Rect bounds = BoundsOf(shape);
                if (!bounds.Overlaps(Bounds)) return;
                var first = new List<Vector2>(8);
                var second = new List<Vector2>(8);
                foreach (RoadTriangle cover in NearbyTriangles(bounds))
                {
                    if (!bounds.Overlaps(cover.Bounds))
                        continue;
                    ClipToRoadTriangle(shape, cover, first, second);
                    // An asphalt triangle can straddle tiles. Each tile owns just its
                    // rectangle even when both tiles receive the same marking record.
                    if (!Bounds.Contains(bounds.min) || !Bounds.Contains(bounds.max))
                    {
                        var sw = Bounds.min;
                        var ne = Bounds.max;
                        var se = new Vector2(ne.x, sw.y);
                        var nw = new Vector2(sw.x, ne.y);
                        Clip(second, first, sw, se);
                        Clip(first, second, se, ne);
                        Clip(second, first, ne, nw);
                        Clip(first, second, nw, sw);
                    }
                    if (second.Count < 3)
                        continue;

                    int vertex = vertices.Count;
                    foreach (Vector2 p in second)
                    {
                        vertices.Add(new Vector3(p.x, height, p.y));
                        if (useCoverage) edgeDistances.Add(edges.At(p));
                    }
                    List<int> material = indices[(int)colour];
                    for (int i = 1; i + 1 < second.Count; i++)
                    {
                        float turn = Cross(second[i] - second[0], second[i + 1] - second[0]);
                        if (Mathf.Abs(turn) <= MinArea)
                            continue;
                        material.Add(vertex);
                        material.Add(vertex + (turn < 0 ? i : i + 1));
                        material.Add(vertex + (turn < 0 ? i + 1 : i));
                    }
                }
            }

            private void DrawOutsideCrossings(IReadOnlyList<Vector2> shape, PaintColour colour, int start, PaintEdges edges)
            {
                if (shape.Count < 3 || Area(shape) <= MinArea)
                    return;

                Rect bounds = BoundsOf(shape);
                for (int i = start; i < crossingAreas.Count; i++)
                {
                    var crossing = crossingAreas[i];
                    if (!bounds.Overlaps(crossing.Bounds))
                        continue;

                    // Peel off the outside of each edge. The remainder inside
                    // all four half-planes belongs to the crossing and is dropped.
                    var remaining = new List<Vector2>(shape);
                    var inside = new List<Vector2>(8);
                    var outside = new List<Vector2>(8);
                    Vector2[] outline = crossing.Outline;
                    for (int edge = 0; edge < outline.Length && remaining.Count >= 3; edge++)
                    {
                        Vector2 a = outline[edge], b = outline[(edge + 1) % outline.Length];
                        Clip(remaining, outside, b, a);
                        DrawOutsideCrossings(outside, colour, i + 1, edges);
                        Clip(remaining, inside, a, b);
                        var buffer = remaining;
                        remaining = inside;
                        inside = buffer;
                    }
                    return;
                }
                EmitOnRoad(shape, colour, edges);
            }

            private bool FitsAsphalt(List<Vector2[]> shapes, Vector2 offset)
            {
                var first = new List<Vector2>(8);
                var second = new List<Vector2>(8);
                foreach (Vector2[] shape in shapes)
                {
                    Vector2[] shifted = System.Array.ConvertAll(shape, point => point + offset);
                    Rect bounds = BoundsOf(shifted);
                    float area = Area(shifted);
                    float covered = 0f;
                    foreach (RoadTriangle cover in NearbyTriangles(bounds))
                    {
                        if (!bounds.Overlaps(cover.Bounds))
                            continue;
                        ClipToRoadTriangle(shifted, cover, first, second);
                        covered += Area(second);
                    }
                    if (area - covered > 0.0001f)
                        return false;
                }
                return true;
            }

            private static float Area(IReadOnlyList<Vector2> points)
            {
                float twiceArea = 0f;
                for (int i = 1; i + 1 < points.Count; i++)
                    twiceArea += Cross(points[i] - points[0], points[i + 1] - points[0]);
                return Mathf.Abs(twiceArea) * 0.5f;
            }

            private static void ClipToRoadTriangle(IReadOnlyList<Vector2> shape, RoadTriangle cover,
                List<Vector2> first, List<Vector2> second)
            {
                first.Clear();
                first.AddRange(shape);
                Clip(first, second, cover.A, cover.B);
                Clip(second, first, cover.B, cover.C);
                Clip(first, second, cover.C, cover.A);
            }

            public Mesh CreateMesh(System.Action<Mesh> ownMesh)
            {
                var mesh = new Mesh { name = "LTA procedural road markings", hideFlags = HideFlags.DontSave };
                ownMesh(mesh);
                if (vertices.Count > 65535)
                    mesh.indexFormat = IndexFormat.UInt32;
                mesh.SetVertices(vertices);
                if (useCoverage) mesh.SetUVs(0, edgeDistances);
                mesh.subMeshCount = indices.Length;
                for (int i = 0; i < indices.Length; i++)
                    mesh.SetTriangles(indices[i], i);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                return mesh;
            }

            private static void Clip(List<Vector2> input, List<Vector2> output, Vector2 a, Vector2 b)
            {
                output.Clear();
                if (input.Count == 0)
                    return;
                Vector2 previous = input[input.Count - 1];
                float before = Cross(b - a, previous - a);
                foreach (Vector2 current in input)
                {
                    float after = Cross(b - a, current - a);
                    if ((before >= 0f) != (after >= 0f))
                        output.Add(Vector2.Lerp(previous, current, before / (before - after)));
                    if (after >= 0f)
                        output.Add(current);
                    previous = current;
                    before = after;
                }
            }

            private static Rect BoundsOf(IReadOnlyList<Vector2> points)
            {
                Vector2 min = points[0], max = points[0];
                for (int i = 1; i < points.Count; i++)
                {
                    min = Vector2.Min(min, points[i]);
                    max = Vector2.Max(max, points[i]);
                }
                return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            }

            private static Vector2 Xz(Vector3 p) => new Vector2(p.x, p.z);
            private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

            private readonly struct RoadTriangle
            {
                public readonly Vector2 A, B, C;
                public readonly Rect Bounds;

                public RoadTriangle(Vector2 a, Vector2 b, Vector2 c)
                {
                    A = a;
                    B = b;
                    C = c;
                    Vector2 min = Vector2.Min(a, Vector2.Min(b, c));
                    Vector2 max = Vector2.Max(a, Vector2.Max(b, c));
                    Bounds = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
                }
            }
        }
    }
}
