using System;
using System.Collections.Generic;
using Formula9.Backend;
using UnityEngine;
using UnityEngine.Rendering;

namespace Formula9.World
{
    // Raised sidewalks from the walkways file (backend src.traffic.import_walkways), one mesh per 250 m tile.
    // The importer supplies the paving layout. Runtime kerb alignment matches it to the active roads and this class
    // lifts its triangles to the kerb's height, slopes the ramps where crossings meet them, and stands
    // vertical faces on its edges: kerb faces down to the road, outer faces down to the ground, and the
    // sides of each ramp.
    public static class SidewalkMesh
    {
        public const int FootpathLayer = 0, KerbStoneLayer = 1, VergeLayer = 2, KerbFaceLayer = 3, OuterFaceLayer = 4, LayerCount = 5;
        // These must equal the importer's (a backend test reads them from here)
        public const float KerbRiseM = 0.15f, KerbStoneM = 0.15f, RampDepthM = 1.5f;
        // The top of the paving, a kerb's rise above the asphalt
        public const float TopY = RoadMesh.SurfaceY + KerbRiseM;
        // A face lower than this is not drawn: where a ramp meets the road the kerb has gone
        private const float MinFaceM = 1e-3f;

        public static Mesh Build(CityWalkwayData data, int tile) => Build(data.Tiles[tile]);

        public static Mesh Build(CityWalkwayTile tile, Func<Vector2, Vector2> align = null)
        {
            Vector2 At(int corner) => AlignedCorner(tile, corner, align);
            var vertices = new List<Vector3>();
            var layers = new List<int>[LayerCount];
            for (var i = 0; i < LayerCount; i++) layers[i] = new List<int>();

            // Flat zones share their corners within the zone, all at the top
            void Flat(int[] triangles, List<int> into)
            {
                var made = new Dictionary<int, int>();
                foreach (var corner in triangles)
                {
                    if (!made.TryGetValue(corner, out var index))
                    {
                        index = made[corner] = vertices.Count;
                        vertices.Add(Ground(At(corner), TopY));
                    }
                    into.Add(index);
                }
            }
            Flat(tile.Footpath, layers[FootpathLayer]);
            Flat(tile.KerbStone, layers[KerbStoneLayer]);
            Flat(tile.Verge, layers[VergeLayer]);

            // Ramps are paved like the footpath, each sloping down to the road from its own kerb
            var sloped = new Dictionary<(int Corner, int Ramp), int>();
            for (var i = 0; i < tile.RampTriangles.Length; i += 4)
            {
                var ramp = tile.RampTriangles[i + 3];
                for (var k = 0; k < 3; k++)
                {
                    var corner = tile.RampTriangles[i + k];
                    if (!sloped.TryGetValue((corner, ramp), out var index))
                    {
                        var at = At(corner);
                        index = sloped[(corner, ramp)] = vertices.Count;
                        vertices.Add(Ground(at, RampHeight(tile.Ramps, ramp, Corner(tile, corner))));
                    }
                    layers[FootpathLayer].Add(index);
                }
            }

            float Top(Vector2 at, int ramp) => ramp < 0 ? TopY : RampHeight(tile.Ramps, ramp, at);
            Faces(tile, tile.KerbEdges, vertices, layers[KerbFaceLayer], (at, ramp) => RoadMesh.SurfaceY, Top, align);
            Faces(tile, tile.OuterEdges, vertices, layers[OuterFaceLayer], (at, ramp) => 0f, Top, align);
            Faces(tile, tile.CheekEdges, vertices, layers[KerbFaceLayer], Top, (at, ramp) => TopY, align);

            // World x and z in metres, so paving and grain are the same size everywhere
            var uvs = new List<Vector2>(vertices.Count);
            foreach (var vertex in vertices) uvs.Add(new Vector2(vertex.x, vertex.z));
            var mesh = new Mesh { name = $"Sidewalks {tile.KeyX},{tile.KeyY}", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = LayerCount;
            for (var i = 0; i < LayerCount; i++) mesh.SetTriangles(layers[i], i, false);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // The height of a ramp's surface: the road's at its kerb, rising to the top over its depth
        public static float RampHeight(int[] ramps, int ramp, Vector2 at)
        {
            var kerb = new Vector2(ramps[ramp * 4], ramps[ramp * 4 + 1]) / 100f;
            var outward = new Vector2(ramps[ramp * 4 + 2], ramps[ramp * 4 + 3]) / 10000f;
            var into = Vector2.Dot(at - kerb, outward);
            return RoadMesh.SurfaceY + KerbRiseM * Mathf.Clamp01(into / RampDepthM);
        }

        public static Vector2 Corner(CityWalkwayTile tile, int corner) =>
            new Vector2(tile.VerticesCm[corner * 2], tile.VerticesCm[corner * 2 + 1]) / 100f;

        internal static Vector2 AlignedCorner(CityWalkwayTile tile, int corner, Func<Vector2, Vector2> align)
        {
            var point = Corner(tile, corner);
            return align != null ? align(point) : point;
        }

        // A vertical quad on each edge from its bottom to its top, facing out to the right of from->to
        private static void Faces(CityWalkwayTile tile, int[] edges, List<Vector3> vertices, List<int> triangles,
            Func<Vector2, int, float> bottom, Func<Vector2, int, float> top, Func<Vector2, Vector2> align)
        {
            for (var i = 0; i < edges.Length; i += 3)
            {
                var a = Corner(tile, edges[i]); var b = Corner(tile, edges[i + 1]); var ramp = edges[i + 2];
                float aLow = bottom(a, ramp), aHigh = top(a, ramp), bLow = bottom(b, ramp), bHigh = top(b, ramp);
                if (aHigh - aLow < MinFaceM && bHigh - bLow < MinFaceM) continue;
                a = align != null ? align(a) : a;
                b = align != null ? align(b) : b;
                var along = b - a;
                var outward = new Vector3(along.y, 0, -along.x);
                var start = vertices.Count;
                vertices.Add(Ground(a, aLow)); vertices.Add(Ground(a, aHigh)); vertices.Add(Ground(b, bHigh)); vertices.Add(Ground(b, bLow));
                // A face that tapers to nothing at one end is one triangle, not a quad with a degenerate half
                if (aHigh - aLow >= MinFaceM) Triangle(vertices, triangles, start, start + 1, start + 2, outward);
                if (bHigh - bLow >= MinFaceM) Triangle(vertices, triangles, start, start + 2, start + 3, outward);
            }
        }

        private static void Triangle(List<Vector3> vertices, List<int> triangles, int a, int b, int c, Vector3 outward)
        {
            var normal = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
            if (normal.sqrMagnitude < 1e-12f) return;
            if (Vector3.Dot(normal, outward) >= 0) triangles.AddRange(new[] { a, b, c });
            else triangles.AddRange(new[] { a, c, b });
        }

        private static Vector3 Ground(Vector2 point, float height) => new Vector3(point.x, height, point.y);
    }

    // How high the sidewalk is under a point, for things that stand on it: traffic light poles, bus
    // shelters and pedestrians. Triangles are filed in a grid, so a lookup only tries a few.
    public sealed class SidewalkSurface
    {
        private const float CellM = 8f;
        private readonly List<Vector2> corners = new List<Vector2>();
        private readonly List<float> heights = new List<float>();
        private readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();

        public int TriangleCount => corners.Count / 3;

        // Pure C# over the parsed data, so it can be built off the main thread
        public static SidewalkSurface Build(CityWalkwayData data, Func<Vector2, Vector2> align = null)
        {
            var surface = new SidewalkSurface();
            foreach (var tile in data.Tiles)
            {
                foreach (var zone in new[] { tile.Footpath, tile.KerbStone, tile.Verge })
                    for (var i = 0; i < zone.Length; i += 3)
                        surface.Add(tile, zone[i], zone[i + 1], zone[i + 2], -1, align);
                for (var i = 0; i < tile.RampTriangles.Length; i += 4)
                    surface.Add(tile, tile.RampTriangles[i], tile.RampTriangles[i + 1], tile.RampTriangles[i + 2], tile.RampTriangles[i + 3], align);
            }
            return surface;
        }

        private void Add(CityWalkwayTile tile, int a, int b, int c, int ramp, Func<Vector2, Vector2> align)
        {
            var index = corners.Count / 3;
            foreach (var corner in new[] { a, b, c })
            {
                var at = SidewalkMesh.Corner(tile, corner);
                corners.Add(align != null ? align(at) : at);
                heights.Add(ramp < 0 ? SidewalkMesh.TopY : SidewalkMesh.RampHeight(tile.Ramps, ramp, at));
            }
            var p0 = corners[index * 3]; var p1 = corners[index * 3 + 1]; var p2 = corners[index * 3 + 2];
            var min = Vector2.Min(p0, Vector2.Min(p1, p2)); var max = Vector2.Max(p0, Vector2.Max(p1, p2));
            for (var x = Mathf.FloorToInt(min.x / CellM); x <= Mathf.FloorToInt(max.x / CellM); x++)
                for (var y = Mathf.FloorToInt(min.y / CellM); y <= Mathf.FloorToInt(max.y / CellM); y++)
                {
                    var key = Key(x, y);
                    if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<int>();
                    list.Add(index);
                }
        }

        // The sidewalk's height at a point (x east, y north), if the point is on it
        public bool TryHeight(Vector2 at, out float height)
        {
            height = 0;
            if (!cells.TryGetValue(Key(Mathf.FloorToInt(at.x / CellM), Mathf.FloorToInt(at.y / CellM)), out var list)) return false;
            foreach (var index in list)
            {
                var a = corners[index * 3]; var b = corners[index * 3 + 1]; var c = corners[index * 3 + 2];
                var area = Cross(b - a, c - a);
                if (Mathf.Abs(area) < 1e-8f) continue;
                var u = Cross(b - at, c - at) / area;
                var v = Cross(c - at, a - at) / area;
                var w = 1 - u - v;
                const float slack = -1e-4f;
                if (u < slack || v < slack || w < slack) continue;
                height = u * heights[index * 3] + v * heights[index * 3 + 1] + w * heights[index * 3 + 2];
                return true;
            }
            return false;
        }

        public float HeightOr(Vector2 at, float otherwise) => TryHeight(at, out var height) ? height : otherwise;

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;
    }
}
