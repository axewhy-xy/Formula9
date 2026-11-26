using System;
using System.Collections.Generic;
using System.Threading;
using Formula9.Backend;
using UnityEngine;

namespace Formula9.World
{
    internal static class SidewalkRoadSeam
    {
        private const float CellSize = 4f;
        private const float MaxShift = .65f;
        private const float MaxGreenGap = 2f;
        private const float BoundaryProbeM = .05f;

        internal static (Func<Vector2, Vector2> Align, Vector3[] Green) Build(CityWalkwayData data, Vector3[] vertices,
            int[] triangles, CancellationToken cancellation)
        {
            var counts = new Dictionary<(Vector2 A, Vector2 B), int>();
            var surfaces = new List<(Vector2 A, Vector2 B, Vector2 C)>();
            var surfaceGrid = new Dictionary<Vector2Int, List<int>>();

            for (int i = 0; i < triangles.Length; i += 3)
            {
                cancellation.ThrowIfCancellationRequested();
                var a = vertices[triangles[i]];
                var b = vertices[triangles[i + 1]];
                var c = vertices[triangles[i + 2]];

                // Recessed median faces are underneath the road and are not curb targets.
                if (Mathf.Min(a.y, Mathf.Min(b.y, c.y)) < RoadMesh.SurfaceY - .01f) continue;

                var horizontalA = Xz(a);
                var horizontalB = Xz(b);
                var horizontalC = Xz(c);
                Count(horizontalA, horizontalB);
                Count(horizontalB, horizontalC);
                Count(horizontalC, horizontalA);

                int surface = surfaces.Count;
                surfaces.Add((horizontalA, horizontalB, horizontalC));
                var low = Cell(Vector2.Min(horizontalA, Vector2.Min(horizontalB, horizontalC)));
                var high = Cell(Vector2.Max(horizontalA, Vector2.Max(horizontalB, horizontalC)));
                for (int x = low.x; x <= high.x; x++)
                for (int y = low.y; y <= high.y; y++)
                {
                    var key = new Vector2Int(x, y);
                    if (!surfaceGrid.TryGetValue(key, out var list))
                        surfaceGrid.Add(key, list = new List<int>());
                    list.Add(surface);
                }
            }

            var grid = new Dictionary<Vector2Int, List<(Vector2 A, Vector2 B)>>();
            foreach (var entry in counts)
            {
                if (entry.Value != 1) continue;
                var edge = entry.Key;

                // Clipped road pieces can leave a one-triangle edge along a seam.
                // A real perimeter has asphalt on only one side of the edge.
                if (!IsExteriorBoundary(edge)) continue;

                Vector2 pad = Vector2.one * MaxGreenGap;
                var low = Cell(Vector2.Min(edge.A, edge.B) - pad);
                var high = Cell(Vector2.Max(edge.A, edge.B) + pad);
                for (int x = low.x; x <= high.x; x++)
                for (int y = low.y; y <= high.y; y++)
                {
                    var key = new Vector2Int(x, y);
                    if (!grid.TryGetValue(key, out var list))
                        grid.Add(key, list = new List<(Vector2, Vector2)>());
                    list.Add(edge);
                }
            }

            var adjusted = new Dictionary<Vector2, Vector2>();
            foreach (var tile in data.Tiles)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int i = 0; i < tile.KerbEdges.Length; i += 3)
                {
                    var a = SidewalkMesh.Corner(tile, tile.KerbEdges[i]);
                    var b = SidewalkMesh.Corner(tile, tile.KerbEdges[i + 1]);
                    Vector2 direction = (b - a).normalized;
                    Vector2 towardRoad = new Vector2(direction.y, -direction.x);
                    Snap(a, direction, towardRoad);
                    Snap(b, direction, towardRoad);
                }
            }

            Vector2 Align(Vector2 at) => adjusted.TryGetValue(at, out var target) ? target : at;

            var green = new List<Vector3>();
            foreach (var tile in data.Tiles)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int i = 0; i < tile.KerbEdges.Length; i += 3)
                {
                    var a = Align(SidewalkMesh.Corner(tile, tile.KerbEdges[i]));
                    var b = Align(SidewalkMesh.Corner(tile, tile.KerbEdges[i + 1]));
                    var direction = (b - a).normalized;
                    var towardRoad = new Vector2(direction.y, -direction.x);
                    if (!FindRoadSide(a, towardRoad, direction, out var roadA) ||
                        !FindRoadSide(b, towardRoad, direction, out var roadB)) continue;
                    if (Vector2.Dot(roadB - roadA, direction) < 0f) continue;
                    AddGreenTriangle(a, b, roadB);
                    AddGreenTriangle(a, roadB, roadA);
                }
            }

            return (Align, green.ToArray());

            bool FindRoadSide(Vector2 at, Vector2 towardRoad, Vector2 direction, out Vector2 target)
            {
                target = at;
                if (!grid.TryGetValue(Cell(at), out var candidates)) return false;
                float best = MaxGreenGap * MaxGreenGap;
                bool found = false;
                foreach (var edge in candidates)
                {
                    var delta = edge.B - edge.A;
                    if (Mathf.Abs(Vector2.Dot(delta.normalized, direction)) < .94f) continue;
                    float t = Vector2.Dot(at - edge.A, delta) / delta.sqrMagnitude;
                    var point = edge.A + Mathf.Clamp01(t) * delta;
                    var offset = point - at;
                    float distance = offset.sqrMagnitude;
                    if (distance >= best || Vector2.Dot(offset, towardRoad) < -.02f) continue;
                    if (Mathf.Abs(Vector2.Dot(offset, direction)) > .35f) continue;
                    best = distance;
                    target = point;
                    found = true;
                }
                return found;
            }

            bool IsExteriorBoundary((Vector2 A, Vector2 B) edge)
            {
                var delta = edge.B - edge.A;
                if (delta.sqrMagnitude < .0025f) return false;
                var normal = new Vector2(-delta.y, delta.x).normalized;
                var middle = (edge.A + edge.B) * .5f;
                bool left = HasSurfaceAt(middle + normal * BoundaryProbeM);
                bool right = HasSurfaceAt(middle - normal * BoundaryProbeM);
                return left != right;
            }

            bool HasSurfaceAt(Vector2 point)
            {
                if (!surfaceGrid.TryGetValue(Cell(point), out var candidates)) return false;
                foreach (int index in candidates)
                {
                    var surface = surfaces[index];
                    float ab = Cross(surface.B - surface.A, point - surface.A);
                    float bc = Cross(surface.C - surface.B, point - surface.B);
                    float ca = Cross(surface.A - surface.C, point - surface.C);
                    if ((ab >= -1e-5f && bc >= -1e-5f && ca >= -1e-5f) ||
                        (ab <= 1e-5f && bc <= 1e-5f && ca <= 1e-5f)) return true;
                }
                return false;
            }

            void AddGreenTriangle(Vector2 a, Vector2 b, Vector2 c)
            {
                float cross = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
                if (Mathf.Abs(cross) < .0001f) return;
                const float height = RoadMesh.SurfaceY - .025f;
                green.Add(new Vector3(a.x, height, a.y));
                green.Add(cross < 0f ? new Vector3(b.x, height, b.y) : new Vector3(c.x, height, c.y));
                green.Add(cross < 0f ? new Vector3(c.x, height, c.y) : new Vector3(b.x, height, b.y));
            }

            void Count(Vector2 a, Vector2 b)
            {
                if ((a - b).sqrMagnitude < .0025f) return;
                var key = a.x < b.x || (a.x == b.x && a.y < b.y) ? (a, b) : (b, a);
                counts.TryGetValue(key, out int count);
                counts[key] = count + 1;
            }

            void Snap(Vector2 at, Vector2 direction, Vector2 towardRoad)
            {
                if (!grid.TryGetValue(Cell(at), out var candidates)) return;
                float best = adjusted.TryGetValue(at, out var current)
                    ? (current - at).sqrMagnitude : MaxShift * MaxShift;
                foreach (var edge in candidates)
                {
                    Vector2 line = edge.B - edge.A;
                    if (Mathf.Abs(Vector2.Dot(line.normalized, direction)) < .94f) continue;
                    float t = Vector2.Dot(at - edge.A, line) / line.sqrMagnitude;
                    Vector2 point = edge.A + Mathf.Clamp01(t) * line;
                    Vector2 offset = point - at;
                    if (Vector2.Dot(offset, towardRoad) < -.02f ||
                        Mathf.Abs(Vector2.Dot(offset, direction)) > .35f) continue;
                    float distance = offset.sqrMagnitude;
                    if (distance >= best) continue;
                    best = distance;
                    adjusted[at] = point;
                }
            }
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
        private static Vector2 Xz(Vector3 point) => new Vector2(point.x, point.z);
        private static Vector2Int Cell(Vector2 point) =>
            new Vector2Int(Mathf.FloorToInt(point.x / CellSize), Mathf.FloorToInt(point.y / CellSize));
    }
}
