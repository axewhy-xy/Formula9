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

        internal static Func<Vector2, Vector2> Build(CityWalkwayData data, Vector3[] vertices,
            int[] triangles, CancellationToken cancellation)
        {
            var counts = new Dictionary<(Vector2 A, Vector2 B), int>();
            for (int i = 0; i < triangles.Length; i += 3)
            {
                cancellation.ThrowIfCancellationRequested();
                var a = vertices[triangles[i]];
                var b = vertices[triangles[i + 1]];
                var c = vertices[triangles[i + 2]];
                // Recessed median faces are underneath the road and are not curb targets.
                if (Mathf.Min(a.y, Mathf.Min(b.y, c.y)) < RoadMesh.SurfaceY - .01f) continue;
                Count(Xz(a), Xz(b));
                Count(Xz(b), Xz(c));
                Count(Xz(c), Xz(a));
            }

            var grid = new Dictionary<Vector2Int, List<(Vector2 A, Vector2 B)>>();
            foreach (var entry in counts)
            {
                if (entry.Value != 1) continue;
                var edge = entry.Key;
                Vector2 pad = Vector2.one * MaxShift;
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
                    Snap(a, direction);
                    Snap(b, direction);
                }
            }
            return at => adjusted.TryGetValue(at, out var target) ? target : at;

            void Count(Vector2 a, Vector2 b)
            {
                if ((a - b).sqrMagnitude < .0025f) return;
                var key = a.x < b.x || (a.x == b.x && a.y < b.y) ? (a, b) : (b, a);
                counts.TryGetValue(key, out int count);
                counts[key] = count + 1;
            }

            void Snap(Vector2 at, Vector2 direction)
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
                    float distance = (point - at).sqrMagnitude;
                    if (distance >= best) continue;
                    best = distance;
                    adjusted[at] = point;
                }
            }
        }

        private static Vector2 Xz(Vector3 point) => new Vector2(point.x, point.z);
        private static Vector2Int Cell(Vector2 point) =>
            new Vector2Int(Mathf.FloorToInt(point.x / CellSize), Mathf.FloorToInt(point.y / CellSize));
    }
}
