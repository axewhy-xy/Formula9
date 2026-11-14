using System.Collections.Generic;
using System.Linq;
using Formula9.Backend;
using UnityEngine;
using UnityEngine.Rendering;

namespace Formula9.World
{
    public static class RoadMesh
    {
        public const int KerbLayer = 0, AsphaltLayer = 1, MarkingLayer = 2, LayerCount = 3;
        public const float LaneWidthM = 3.2f;
        public const float ShoulderM = 0.6f, KerbM = 0.55f;
        public const float SurfaceY = 0.1f, KerbSurfaceY = SurfaceY;

        private const float MinSegmentM = 0.001f;
        private const float MaxMitre = 2f;
        private const float ContinuationDot = -0.8f;
        private const float JoinToleranceM = 0.15f;
        private const float CapEdgeErrorM = 0.035f;
        private const float TaperMetresPerMetre = 40f;
        private const float MinimumTaperLengthM = 56f;
        private const float MaxCurveDeviationM = 0.9f;
        private const float MaxCornerSpanM = 16f;

        private struct Corner
        {
            public float Span, Turn, Radius;
            public Vector2 Incoming, Outgoing;
            public bool CanFitRoad;
        }

        private sealed class RoadPath
        {
            public CityRoad Road;
            public List<Vector2> Points;
            public float[] Stations;
            public Vector2[] Offsets;
            public float Width;
            public int Lanes;
            public List<Section> Sections;
            public float[] PaintDistances;
            public float PaintOrigin, PaintDirection;
            public float PaintLength => PaintDistances[PaintDistances.Length - 1];
            public readonly List<WidthBlend> WidthBlends = new List<WidthBlend>();
            public readonly End Start = new End();
            public readonly End Finish = new End();
            public float Length => Stations[Stations.Length - 1];
        }

        private sealed class End
        {
            public bool Joined;
            public int JunctionIndex = -1;
            public bool JunctionPaintBreak;
            public float IntersectionClearance;
            public int SignalJunction = -1;
            public Vector2 LeftEdge, RightEdge;
            public (RoadPath Path, bool AtStart) Across;
            public Vector2 TangentCorrection;
        }

        private struct WidthBlend
        {
            public bool AtStart;
            public float DistanceToJoin, Reach, TargetWidth;

            public float Weight(float distance)
            {
                return Mathf.SmoothStep(0f, 1f,
                    Mathf.Clamp01((Reach - DistanceToJoin - distance) / Reach));
            }
        }

        private struct Section
        {
            public Vector2 Point, Offset;
            public float HalfWidth;
        }

        public static float HalfWidth(CityRoad road) => LaneCount(road) * LaneWidthM * 0.5f + ShoulderM;

        private static int LaneCount(CityRoad road) => Mathf.Max(1, road.Oneway ? road.Lanes : road.Lanes * 2);

        public static Mesh Build(IEnumerable<CityRoad> roads) => Build(roads, out _);

        internal static Mesh Build(IEnumerable<CityRoad> roads, out int clippedTriangles,
            LtaMappedRoadPaint.Result mapped = null)
        {
            var paths = roads.Select(CreatePath).Where(path => path != null).ToList();
            var junctions = ConnectEnds(paths, mapped?.SignalJunctionCenters);
            BuildWidthProfiles(paths);
            // Earlier roads keep the shared area when the asphalt triangles are clipped.
            paths = paths.OrderBy(path => IsUnmarkedClass(path.Road.Class))
                .ThenByDescending(path => path.Lanes)
                .ThenBy(path => path.Road.FromNode)
                .ThenBy(path => path.Road.ToNode)
                .ThenBy(path => path.Points[0].x)
                .ThenBy(path => path.Points[0].y)
                .ThenBy(path => path.Points[path.Points.Count - 1].x)
                .ThenBy(path => path.Points[path.Points.Count - 1].y)
                .ToList();

            foreach (var path in paths)
            {
                path.Sections = Sample(path);
                path.PaintDistances = new float[path.Sections.Count];
                for (int i = 1; i < path.Sections.Count; i++)
                    path.PaintDistances[i] = path.PaintDistances[i - 1] +
                        Vector2.Distance(path.Sections[i - 1].Point, path.Sections[i].Point);
            }
            SetDashPhases(paths);

            var vertices = new List<Vector3>();
            var laneUV = new List<Vector4>();
            var paintBounds = new List<Vector4>();
            var barDistances = new List<Vector2>();
            var layers = Enumerable.Range(0, LayerCount).Select(_ => new List<int>()).ToArray();

            int AddVertex(Vector2 point, Vector4 lane = default, Vector4 bounds = default, Vector2 bars = default)
            {
                int index = vertices.Count;
                vertices.Add(new Vector3(point.x, SurfaceY, point.y));
                laneUV.Add(lane);
                paintBounds.Add(bounds);
                barDistances.Add(bars);
                return index;
            }

            void AddTriangle(int a, int b, int c, int layer)
            {
                float normalY = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).y;
                if (Mathf.Abs(normalY) < 0.000001f) return;
                var triangles = layers[layer];
                triangles.Add(a);
                triangles.Add(normalY > 0 ? b : c);
                triangles.Add(normalY > 0 ? c : b);
            }

            void AddQuad(int leftA, int rightA, int leftB, int rightB, int layer)
            {
                AddTriangle(leftA, leftB, rightB, layer);
                AddTriangle(leftA, rightB, rightA, layer);
            }

            void AddCap(Vector2 point, float width)
            {
                int center = AddVertex(point);
                float halfAngle = Mathf.Acos(Mathf.Clamp(1f - CapEdgeErrorM / width, -1f, 1f));
                int sides = Mathf.Clamp(Mathf.CeilToInt(Mathf.PI / halfAngle), 16, 64);
                for (int i = 0; i < sides; i++)
                {
                    float angle = 2 * Mathf.PI * i / sides;
                    var edge = point + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * width;
                    AddVertex(edge);
                }
                for (int i = 0; i < sides; i++)
                    AddTriangle(center, center + 1 + i, center + 1 + (i + 1) % sides, AsphaltLayer);
            }

            foreach (var path in paths)
            {
                var sections = path.Sections;
                float length = path.PaintLength;
                float startStation = PaintClearance(path.Start, sections[0].HalfWidth, length);
                float endStation = length - PaintClearance(path.Finish, sections[sections.Count - 1].HalfWidth, length);
                float lanes = IsUnmarkedClass(path.Road.Class) ? 0f : path.Lanes;
                // Internal links of a divided intersection can have separate
                // graph nodes at either end. Keep their entire surface unpainted.
                bool sameSignalJunction = path.Start.SignalJunction >= 0 &&
                    path.Start.SignalJunction == path.Finish.SignalJunction;
                bool internalLink = sameSignalJunction ||
                    (path.Start.JunctionPaintBreak && path.Finish.JunctionPaintBreak &&
                        length <= Mathf.Max(2f * path.Width + 2f,
                            path.Start.IntersectionClearance + path.Finish.IntersectionClearance + 3f));
                if (internalLink) lanes = 0f;

                // 1 = full one-way approach, 2 = left half, 3 = right half
                // of the mesh in its FromNode-to-ToNode direction.
                float startBar = !path.Road.Oneway && path.Start.JunctionPaintBreak ? 3f : 0f;
                float endBar = path.Finish.JunctionPaintBreak ? (path.Road.Oneway ? 1f : 2f) : 0f;

                bool forward = path.PaintDirection > 0f;
                float startPaint = path.PaintOrigin + path.PaintDirection * startStation;
                float endPaint = path.PaintOrigin + path.PaintDirection * endStation;
                var bounds = startStation < endStation
                    ? new Vector4(Mathf.Min(startPaint, endPaint), Mathf.Max(startPaint, endPaint),
                        forward ? startBar : endBar, forward ? endBar : startBar)
                    : new Vector4(1f, 0f, 0f, 0f);
                var lowBar = bounds.z > 0f ? StopPlane(path, forward ? startStation : endStation, forward) : default;
                var highBar = bounds.w > 0f ? StopPlane(path, forward ? endStation : startStation, !forward) : default;
                Vector2 BarDistances(Vector2 point) =>
                    new Vector2(lowBar.DistanceTo(point), highBar.DistanceTo(point));

                int previous = -1;
                for (int i = 0; i < sections.Count; i++)
                {
                    var section = sections[i];
                    var point = section.Point;
                    var edge = section.Offset * section.HalfWidth;
                    var outer = section.Offset * (section.HalfWidth + KerbM);
                    if (i == 0) SetEdges(path.Start, point, edge);
                    if (i == sections.Count - 1) SetEdges(path.Finish, point, edge);
                    int first = AddVertex(point + outer);
                    float width = 2f * edge.magnitude;
                    float along = path.PaintOrigin + path.PaintDirection * path.PaintDistances[i];
                    AddVertex(point + edge, new Vector4(0f, along, lanes, width), bounds, BarDistances(point + edge));
                    AddVertex(point - edge, new Vector4(width, along, lanes, width), bounds, BarDistances(point - edge));
                    AddVertex(point - outer);
                    if (previous >= 0)
                    {
                        AddQuad(previous, previous + 1, first, first + 1, KerbLayer);
                        AddQuad(previous + 1, previous + 2, first + 1, first + 2, AsphaltLayer);
                        AddQuad(previous + 2, previous + 3, first + 2, first + 3, KerbLayer);
                    }
                    previous = first;
                }
            }

            void SetEdges(End end, Vector2 point, Vector2 offset)
            {
                end.LeftEdge = point + offset;
                end.RightEdge = point - offset;
            }

            // Junctions use the actual strip cross-sections. Build a single
            // patch across their edges; the clipping pass removes any overlap.
            foreach (var junction in junctions)
            {
                var perimeter = JunctionPerimeter(junction);
                if (perimeter.Count < 3) continue;
                int first = vertices.Count;
                foreach (var point in perimeter) AddVertex(point);
                for (int i = 1; i < perimeter.Count - 1; i++)
                    AddTriangle(first, first + i, first + i + 1, AsphaltLayer);
            }

            foreach (var path in paths)
            {
                if (path.Start.JunctionIndex < 0) AddCap(path.Points[0], path.Width);
                if (path.Finish.JunctionIndex < 0) AddCap(path.Points[path.Points.Count - 1], path.Width);
            }

            clippedTriangles = TrimAsphaltOverlaps(layers[AsphaltLayer], vertices, laneUV, paintBounds, barDistances);

            var mesh = new Mesh { name = "City roads", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(vertices);
            // The fallback asphalt shader reads UV0 for its grain texture.
            mesh.SetUVs(0, vertices.Select(vertex => new Vector2(vertex.x, vertex.z) / 8f).ToList());
            mesh.SetUVs(1, laneUV);
            mesh.SetUVs(2, paintBounds);
            mesh.SetUVs(3, barDistances);
            mesh.subMeshCount = LayerCount;
            for (int i = 0; i < LayerCount; i++) mesh.SetTriangles(layers[i], i);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private const float DashPeriodM = 6f; // Match CityRoadTexture.shader.

        private static void SetDashPhases(List<RoadPath> paths)
        {
            // Walk open chains from an end, then seed any closed loops.
            foreach (var path in paths)
            {
                if (path.PaintDirection != 0f) continue;
                if (!ContinuesPaint(path.Start)) Walk(path, true);
                else if (!ContinuesPaint(path.Finish)) Walk(path, false);
            }
            foreach (var path in paths)
                if (path.PaintDirection == 0f) Walk(path, true);

            void Walk(RoadPath path, bool enterAtStart)
            {
                float phase = 1.5f;
                while (path.PaintDirection == 0f)
                {
                    path.PaintDirection = enterAtStart ? 1f : -1f;
                    path.PaintOrigin = enterAtStart ? phase : phase + path.PaintLength;
                    phase = Mathf.Repeat(phase + path.PaintLength, DashPeriodM);
                    var exit = enterAtStart ? path.Finish : path.Start;
                    if (!ContinuesPaint(exit)) break;
                    (path, enterAtStart) = exit.Across;
                }
            }
        }

        private static bool ContinuesPaint(End end) => end.Joined && !end.JunctionPaintBreak;

        private static float PaintClearance(End end, float halfWidth, float length)
        {
            if (end.JunctionPaintBreak)
                return Mathf.Min(length, Mathf.Max(halfWidth, end.IntersectionClearance));
            return end.Joined ? 0f : Mathf.Min(halfWidth, length * 0.45f);
        }

        private readonly struct BarPlane
        {
            private readonly Vector2 centre, approach;

            public BarPlane(Vector2 centre, Vector2 approach)
            {
                this.centre = centre;
                this.approach = approach;
            }

            public float DistanceTo(Vector2 point) => Vector2.Dot(point - centre, approach);
        }

        private static BarPlane StopPlane(RoadPath path, float station, bool towardsFinish)
        {
            int i = 1;
            // At a sample boundary, use the interval on the painted side.
            while (i < path.PaintDistances.Length - 1 &&
                (path.PaintDistances[i] < station ||
                    (towardsFinish && path.PaintDistances[i] == station))) i++;

            float fraction = EndingDividerFraction(path.Lanes, towardsFinish);
            var a = DividerPoint(path.Sections[i - 1], fraction);
            var b = DividerPoint(path.Sections[i], fraction);
            float t = Mathf.InverseLerp(path.PaintDistances[i - 1], path.PaintDistances[i], station);
            var direction = (b - a).normalized;
            if (direction == Vector2.zero)
                direction = (path.Sections[i].Point - path.Sections[i - 1].Point).normalized;

            return new BarPlane(Vector2.Lerp(a, b, t), towardsFinish ? direction : -direction);
        }

        private static float EndingDividerFraction(int lanes, bool towardsFinish)
        {
            if (lanes < 2) return 0f;
            // Pick the divider closest to the middle of the approach. If two
            // are equally close, prefer the one on the incoming side.
            int divider = towardsFinish ? lanes / 2 : (lanes + 1) / 2;
            return 1f - 2f * divider / lanes;
        }

        private static Vector2 DividerPoint(Section section, float fraction) =>
            section.Point + section.Offset * (section.HalfWidth * fraction);

        private const float OverlapCellM = 32f;
        private const double MinimumDoubleArea = 0.000001;

        private readonly struct AsphaltTriangle
        {
            public readonly Vector2 A, B, C, Min, Max;

            public AsphaltTriangle(Vector2 a, Vector2 b, Vector2 c)
            {
                // Work with a counterclockwise triangle in the horizontal plane.
                A = a; B = b; C = c;
                Min = Vector2.Min(a, Vector2.Min(b, c));
                Max = Vector2.Max(a, Vector2.Max(b, c));
            }

            public bool BoundsOverlap(AsphaltTriangle other) =>
                Min.x < other.Max.x && Max.x > other.Min.x &&
                Min.y < other.Max.y && Max.y > other.Min.y;
        }

        private static int TrimAsphaltOverlaps(List<int> indices, List<Vector3> vertices,
            List<Vector4> laneUV, List<Vector4> paintBounds, List<Vector2> barDistances)
        {
            var triangles = new List<AsphaltTriangle>();
            var grid = new Dictionary<Vector2Int, List<int>>();
            var candidates = new List<int>();
            var seen = new HashSet<int>();
            var output = new List<int>(indices.Count);
            int clippedTriangles = 0;

            Vector2 Point(int index) => new Vector2(vertices[index].x, vertices[index].z);

            for (int i = 0; i < indices.Count; i += 3)
            {
                // The existing mesh faces up in XZ, so reverse its winding for clipping.
                int a = indices[i], b = indices[i + 2], c = indices[i + 1];
                var triangle = new AsphaltTriangle(Point(a), Point(b), Point(c));
                double area = Side(triangle.A, triangle.B, triangle.C);
                if (area <= MinimumDoubleArea) continue;

                var min = OverlapCell(triangle.Min);
                var max = OverlapCell(triangle.Max);
                candidates.Clear();
                seen.Clear();
                for (int x = min.x; x <= max.x; x++)
                for (int y = min.y; y <= max.y; y++)
                {
                    var cell = new Vector2Int(x, y);
                    if (!grid.TryGetValue(cell, out var entries))
                        grid[cell] = entries = new List<int>();
                    foreach (int previous in entries)
                        if (seen.Add(previous) && triangle.BoundsOverlap(triangles[previous]))
                            candidates.Add(previous);
                    entries.Add(triangles.Count);
                }
                triangles.Add(triangle);

                // Subtracting all earlier input triangles is equivalent to subtracting
                // their union, even when some of those triangles were themselves clipped.
                candidates.Sort();
                List<List<Vector2>> pieces = null;
                foreach (int previous in candidates)
                {
                    var clip = triangles[previous];
                    if (pieces == null)
                    {
                        var polygon = new List<Vector2> { triangle.A, triangle.B, triangle.C };
                        if (!HasArea(Intersect(polygon, clip))) continue;
                        pieces = new List<List<Vector2>>();
                        Subtract(polygon, clip, pieces);
                    }
                    else
                    {
                        var remainder = new List<List<Vector2>>();
                        foreach (var polygon in pieces)
                        {
                            if (HasArea(Intersect(polygon, clip))) Subtract(polygon, clip, remainder);
                            else remainder.Add(polygon);
                        }
                        pieces = remainder;
                    }
                    if (pieces.Count == 0) break;
                }

                if (pieces == null)
                {
                    output.Add(a);
                    output.Add(c);
                    output.Add(b);
                    continue;
                }

                clippedTriangles++;
                int Vertex(Vector2 point)
                {
                    int index = vertices.Count;
                    vertices.Add(new Vector3(point.x, SurfaceY, point.y));
                    // Keep the original triangle's lane coordinates across new vertices.
                    float weightB = (float)(Side(triangle.C, triangle.A, point) / area);
                    float weightC = (float)(Side(triangle.A, triangle.B, point) / area);
                    var uv = laneUV[a] + (laneUV[b] - laneUV[a]) * weightB +
                        (laneUV[c] - laneUV[a]) * weightC;
                    laneUV.Add(uv);
                    paintBounds.Add(paintBounds[a]);
                    barDistances.Add(barDistances[a] + (barDistances[b] - barDistances[a]) * weightB +
                        (barDistances[c] - barDistances[a]) * weightC);
                    return index;
                }

                foreach (var polygon in pieces)
                {
                    int first = Vertex(polygon[0]);
                    int previous = Vertex(polygon[1]);
                    for (int j = 2; j < polygon.Count; j++)
                    {
                        int next = Vertex(polygon[j]);
                        if (Side(polygon[0], polygon[j - 1], polygon[j]) > MinimumDoubleArea)
                        {
                            output.Add(first);
                            output.Add(next);
                            output.Add(previous);
                        }
                        previous = next;
                    }
                }
            }

            indices.Clear();
            indices.AddRange(output);
            return clippedTriangles;
        }

        private static Vector2Int OverlapCell(Vector2 point) => new Vector2Int(
            Mathf.FloorToInt(point.x / OverlapCellM), Mathf.FloorToInt(point.y / OverlapCellM));

        private static double Side(Vector2 a, Vector2 b, Vector2 p) =>
            ((double)b.x - a.x) * ((double)p.y - a.y) - ((double)b.y - a.y) * ((double)p.x - a.x);

        private static bool HasArea(List<Vector2> polygon)
        {
            double area = 0;
            for (int i = 2; i < polygon.Count; i++)
                area += Side(polygon[0], polygon[i - 1], polygon[i]);
            return area > MinimumDoubleArea;
        }

        private static List<Vector2> Intersect(List<Vector2> polygon, AsphaltTriangle clip) =>
            ClipHalfPlane(ClipHalfPlane(ClipHalfPlane(polygon, clip.A, clip.B, true),
                clip.B, clip.C, true), clip.C, clip.A, true);

        private static void Subtract(List<Vector2> polygon, AsphaltTriangle clip, List<List<Vector2>> output)
        {
            // Keep each outside slice. Only the remaining inside slice reaches the next edge.
            void TakeOutside(Vector2 a, Vector2 b)
            {
                var outside = ClipHalfPlane(polygon, a, b, false);
                if (HasArea(outside)) output.Add(outside);
            }

            TakeOutside(clip.A, clip.B);
            polygon = ClipHalfPlane(polygon, clip.A, clip.B, true);
            TakeOutside(clip.B, clip.C);
            polygon = ClipHalfPlane(polygon, clip.B, clip.C, true);
            TakeOutside(clip.C, clip.A);
        }

        private static List<Vector2> ClipHalfPlane(List<Vector2> polygon, Vector2 a, Vector2 b, bool inside)
        {
            var output = new List<Vector2>(polygon.Count + 1);
            if (polygon.Count == 0) return output;

            var previous = polygon[polygon.Count - 1];
            double previousSide = Side(a, b, previous);
            foreach (var point in polygon)
            {
                double currentSide = Side(a, b, point);
                bool keepPrevious = inside ? previousSide >= 0 : previousSide <= 0;
                bool keepCurrent = inside ? currentSide >= 0 : currentSide <= 0;
                if (keepPrevious != keepCurrent && previousSide != 0 && currentSide != 0)
                {
                    double t = previousSide / (previousSide - currentSide);
                    output.Add(new Vector2(
                        (float)(previous.x + ((double)point.x - previous.x) * t),
                        (float)(previous.y + ((double)point.y - previous.y) * t)));
                }
                if (keepCurrent) output.Add(point);
                previous = point;
                previousSide = currentSide;
            }
            return output;
        }

        private static RoadPath CreatePath(CityRoad road)
        {
            var points = new List<Vector2>();
            foreach (var (x, y) in road.Points)
            {
                var point = new Vector2((float)x, (float)y);
                if (points.Count == 0 || Vector2.Distance(point, points[points.Count - 1]) > MinSegmentM)
                    points.Add(point);
            }
            if (points.Count < 2) return null;

            var stations = new float[points.Count];
            var offsets = new Vector2[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                if (i > 0) stations[i] = stations[i - 1] + Vector2.Distance(points[i - 1], points[i]);
                var incoming = (points[Mathf.Max(i, 1)] - points[Mathf.Max(i, 1) - 1]).normalized;
                int next = Mathf.Min(i + 1, points.Count - 1);
                var outgoing = (points[next] - points[next - 1]).normalized;
                offsets[i] = Mitre(incoming, outgoing);
            }
            return new RoadPath
            {
                Road = road, Points = points, Stations = stations, Offsets = offsets,
                Width = HalfWidth(road), Lanes = LaneCount(road)
            };
        }

        private static List<List<End>> ConnectEnds(List<RoadPath> paths, List<Vector2> signalCentres)
        {
            var nodes = new Dictionary<long, List<(RoadPath Path, bool AtStart)>>();
            void Add(long node, RoadPath path, bool atStart)
            {
                if (!nodes.TryGetValue(node, out var ends))
                    nodes[node] = ends = new List<(RoadPath, bool)>();
                ends.Add((path, atStart));
            }

            foreach (var path in paths)
            {
                Add(path.Road.FromNode, path, true);
                Add(path.Road.ToNode, path, false);
            }

            var junctions = new List<List<End>>();
            foreach (var ends in nodes.Values)
            {
                var remaining = new List<(RoadPath Path, bool AtStart)>(ends);
                while (remaining.Count > 1)
                {
                    var seed = remaining[0];
                    var origin = Endpoint(seed);
                    var group = remaining.FindAll(end => Vector2.Distance(Endpoint(end), origin) <= JoinToleranceM);
                    foreach (var end in group) remaining.Remove(end);
                    if (group.Count < 2) continue;

                    var junction = new List<End>(group.Count);
                    foreach (var (path, atStart) in group)
                    {
                        var end = atStart ? path.Start : path.Finish;
                        end.JunctionIndex = junctions.Count;
                        junction.Add(end);
                    }
                    junctions.Add(junction);

                    // Choose the straightest compatible pair at this node, even
                    // when a branch also terminates here.
                    float bestDot = ContinuationDot;
                    float bestWidth = 0f;
                    int bestA = -1, bestB = -1;
                    for (int i = 0; i < group.Count; i++)
                    for (int j = i + 1; j < group.Count; j++)
                    {
                        var a = group[i];
                        var b = group[j];
                        if (a.Path == b.Path || a.Path.Road.Oneway != b.Path.Road.Oneway ||
                            (a.Path.Road.Oneway && a.AtStart == b.AtStart) ||
                            Vector2.Distance(Endpoint(a), Endpoint(b)) > JoinToleranceM) continue;
                        float dot = Vector2.Dot(Outward(a), Outward(b));
                        float width = Mathf.Min(a.Path.Width, b.Path.Width);
                        if (dot > bestDot || (dot == bestDot && width <= bestWidth)) continue;
                        bestDot = dot;
                        bestWidth = width;
                        bestA = i;
                        bestB = j;
                    }

                    bool intersection = group.Count >= 4;
                    if (group.Count >= 3 && signalCentres != null)
                        intersection |= signalCentres.Any(point => (point - origin).sqrMagnitude <= 12f * 12f);

                    if (bestA >= 0)
                    {
                        var through = Outward(group[bestA]);
                        for (int i = 0; i < group.Count; i++)
                        {
                            if (i == bestA || i == bestB) continue;
                            if (Mathf.Abs(Vector2.Dot(through, Outward(group[i]))) < 0.70f)
                                intersection = true;
                        }
                        JoinThroughRoad(group[bestA], group[bestB]);
                    }
                    if (!intersection) continue;
                    int signalJunction = NearestSignalJunction(origin, signalCentres);
                    for (int i = 0; i < group.Count; i++)
                    {
                        var end = junction[i];
                        end.JunctionPaintBreak = true;
                        end.SignalJunction = signalJunction;

                        // Move the cutoff to the outer edge of the crossing
                        // carriageway, with a small setback on the approach.
                        var approach = Outward(group[i]);
                        float clearance = group[i].Path.Width;
                        for (int j = 0; j < group.Count; j++)
                        {
                            if (i == j) continue;
                            float cosine = Mathf.Abs(Vector2.Dot(approach, Outward(group[j])));
                            if (cosine >= 0.70f) continue;
                            float sine = Mathf.Sqrt(1f - cosine * cosine);
                            clearance = Mathf.Max(clearance,
                                (group[j].Path.Width + cosine * group[i].Path.Width) / sine);
                        }
                        end.IntersectionClearance = clearance + 1f;
                    }
                }
            }
            return junctions;
        }

        private static int NearestSignalJunction(Vector2 position, List<Vector2> centres)
        {
            if (centres == null) return -1;
            int nearest = -1;
            float best = 18f * 18f;
            for (int i = 0; i < centres.Count; i++)
            {
                float distance = (position - centres[i]).sqrMagnitude;
                if (distance >= best) continue;
                best = distance;
                nearest = i;
            }
            return nearest;
        }

        private static int EndpointIndex((RoadPath Path, bool AtStart) endpoint) =>
            endpoint.AtStart ? 0 : endpoint.Path.Points.Count - 1;

        private static Vector2 Endpoint((RoadPath Path, bool AtStart) endpoint) =>
            endpoint.Path.Points[EndpointIndex(endpoint)];

        private static Vector2 Outward((RoadPath Path, bool AtStart) endpoint) =>
            (endpoint.Path.Points[EndpointIndex(endpoint) + (endpoint.AtStart ? 1 : -1)] -
                Endpoint(endpoint)).normalized;

        private static void JoinThroughRoad((RoadPath Path, bool AtStart) first,
            (RoadPath Path, bool AtStart) second)
        {
            var (a, startA) = first;
            var (b, startB) = second;
            var outwardA = Outward(first);
            var outwardB = Outward(second);
            var point = (Endpoint(first) + Endpoint(second)) * 0.5f;
            var normal = Mitre(-outwardA, outwardB);
            int indexA = EndpointIndex(first);
            int indexB = EndpointIndex(second);
            a.Points[indexA] = b.Points[indexB] = point;
            a.Offsets[indexA] = startA ? -normal : normal;
            b.Offsets[indexB] = startB ? normal : -normal;

            var tangent = (outwardB - outwardA).normalized;
            var endA = startA ? a.Start : a.Finish;
            var endB = startB ? b.Start : b.Finish;
            endA.TangentCorrection = -tangent - outwardA;
            endB.TangentCorrection = tangent - outwardB;
            endA.Joined = endB.Joined = true;
            endA.Across = second;
            endB.Across = first;
        }

        private static List<Vector2> JunctionPerimeter(List<End> ends)
        {
            var points = new List<Vector2>(2 * ends.Count);
            foreach (var end in ends)
            {
                points.Add(end.LeftEdge);
                points.Add(end.RightEdge);
            }
            points.Sort((a, b) => a.x == b.x ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
            var perimeter = new List<Vector2>(points.Count);
            void Append(Vector2 point, int start)
            {
                while (perimeter.Count >= start + 2 &&
                    Side(perimeter[perimeter.Count - 2], perimeter[perimeter.Count - 1], point) <= MinimumDoubleArea)
                    perimeter.RemoveAt(perimeter.Count - 1);
                perimeter.Add(point);
            }
            foreach (var point in points) Append(point, 0);
            int upperStart = perimeter.Count - 1;
            for (int i = points.Count - 2; i >= 0; i--) Append(points[i], upperStart);
            perimeter.RemoveAt(perimeter.Count - 1);
            return perimeter;
        }

        private static void BuildWidthProfiles(List<RoadPath> paths)
        {
            foreach (var path in paths)
            {
                AddTransition(path, true);
                AddTransition(path, false);
            }

            void AddTransition(RoadPath path, bool atStart)
            {
                var end = atStart ? path.Start : path.Finish;
                var other = end.Across.Path;
                // Process each transition from the narrower side exactly once.
                if (other == null || path.Width >= other.Width) return;

                float joinHalfWidth = Mathf.Max(path.Width, other.Width);
                float desired = Mathf.Max(MinimumTaperLengthM,
                    (joinHalfWidth - path.Width) * TaperMetresPerMetre);
                float reach = AvailableReach(path, atStart, desired);
                // Reach the full width at the join, with the entire taper
                // on the narrow side. In reverse this is a gradual narrowing.
                ApplyWidthBlend(path, atStart, reach, joinHalfWidth);
            }
        }

        private static float AvailableReach(RoadPath path, bool atStart, float desired)
        {
            float distance = 0f;
            while (true)
            {
                distance += path.Length;
                if (distance >= 2f * desired) return desired;
                var exit = atStart ? path.Finish : path.Start;
                var (next, nextAtStart) = exit.Across;
                if (next == null) return Mathf.Min(distance, desired);
                // Two widening transitions on the same run share the available
                // length. If the next road is narrower, its own taper is on
                // the other side of that join.
                if (next.Width > path.Width) return Mathf.Min(distance * 0.5f, desired);
                if (next.Width < path.Width) return Mathf.Min(distance, desired);
                path = next;
                atStart = nextAtStart;
            }
        }

        private static void ApplyWidthBlend(RoadPath path, bool atStart, float reach, float targetWidth)
        {
            float distance = 0f;
            while (distance < reach)
            {
                path.WidthBlends.Add(new WidthBlend
                {
                    AtStart = atStart,
                    DistanceToJoin = distance,
                    Reach = reach,
                    TargetWidth = targetWidth
                });
                distance += path.Length;
                var exit = atStart ? path.Finish : path.Start;
                var (next, nextAtStart) = exit.Across;
                if (next == null || next.Width != path.Width) break;
                path = next;
                atStart = nextAtStart;
            }
        }

        private static List<Section> Sample(RoadPath path)
        {
            var stations = path.Stations;
            var distances = new SortedSet<float>(stations);
            var corners = new Corner[stations.Length];
            float maximumHalfWidth = path.Width;
            foreach (var blend in path.WidthBlends)
                maximumHalfWidth = Mathf.Max(maximumHalfWidth, blend.TargetWidth);
            for (int i = 1; i < stations.Length - 1; i++)
            {
                var incoming = (path.Points[i] - path.Points[i - 1]).normalized;
                var outgoing = (path.Points[i + 1] - path.Points[i]).normalized;
                float dot = Mathf.Clamp(Vector2.Dot(incoming, outgoing), -1f, 1f);
                if (dot > 0.9998f || dot < -0.9f) continue;

                float turn = Mathf.Atan2(Vector2.Dot(LeftOf(incoming), outgoing), dot);
                float halfTurn = Mathf.Abs(turn) * 0.5f;
                float tangentFactor = Mathf.Tan(halfTurn);
                float secantMinusOne = 1f / Mathf.Cos(halfTurn) - 1f;
                float room = Mathf.Min(stations[i] - stations[i - 1], stations[i + 1] - stations[i]);
                // The fillet must stay close to the source path and clear adjacent corners.
                float span = Mathf.Min(room * 0.45f, Mathf.Min(MaxCornerSpanM,
                    MaxCurveDeviationM * tangentFactor / secantMinusOne));
                float radius = span / tangentFactor;
                bool canFitRoad = radius > maximumHalfWidth + KerbM;
                if (!canFitRoad)
                {
                    // Keep a bounded offset through tight bends where a circular
                    // inner edge would reverse direction.
                    span = Mathf.Min(span, Mathf.Min(8f, 2.4f / (incoming - outgoing).magnitude));
                }
                corners[i] = new Corner
                {
                    Span = span, Turn = turn, Radius = radius,
                    Incoming = incoming, Outgoing = outgoing, CanFitRoad = canFitRoad
                };
                int steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(Mathf.Abs(turn) / 0.09f, 2f * span / 1.5f)), 6, 32);
                for (int j = 0; j <= steps; j++)
                    distances.Add(stations[i] + span * (2f * j / steps - 1f));
            }

            for (int i = 1; i < corners.Length - 1; i++)
                if (corners[i].CanFitRoad)
                    path.Offsets[i] = Vector2.zero;

            void AddBlendSamples(float span, bool atStart)
            {
                if (span <= 0f) return;
                int steps = Mathf.Max(8, Mathf.CeilToInt(span / 0.75f));
                for (int i = 1; i <= steps; i++)
                {
                    float distance = span * i / steps;
                    distances.Add(atStart ? distance : path.Length - distance);
                }
            }

            float startBlend = path.Start.Joined ? Mathf.Min(8f, stations[1] * 0.45f) : 0f;
            float finishBlend = path.Finish.Joined
                ? Mathf.Min(8f, (path.Length - stations[stations.Length - 2]) * 0.45f) : 0f;
            AddBlendSamples(startBlend, true);
            AddBlendSamples(finishBlend, false);
            foreach (var blend in path.WidthBlends)
                AddBlendSamples(Mathf.Min(path.Length, blend.Reach - blend.DistanceToJoin), blend.AtStart);
            var sections = new List<Section>(distances.Count);
            int segment = 0;
            foreach (float distance in distances)
            {
                while (segment + 1 < stations.Length - 1 && distance > stations[segment + 1])
                    segment++;

                Interpolate(path, segment, distance, out var point, out var offset);
                int index = distance - stations[segment] <= corners[segment].Span && corners[segment].Span > 0f
                    ? segment : segment + 1;
                var corner = corners[index];
                if (corner.Span > 0f && Mathf.Abs(distance - stations[index]) <= corner.Span + MinSegmentM)
                {
                    float station = stations[index];
                    Interpolate(path, index - 1, station - corner.Span, out var before, out var beforeOffset);
                    Interpolate(path, index, station + corner.Span, out var after, out var afterOffset);
                    float t = Mathf.InverseLerp(station - corner.Span, station + corner.Span, distance);
                    if (corner.CanFitRoad)
                    {
                        // An arc meets both straight approaches with a matching tangent.
                        // Its normal is shared by the asphalt and kerb edges.
                        var normal = LeftOf(corner.Incoming);
                        float signedRadius = Mathf.Sign(corner.Turn) * corner.Radius;
                        float angle = corner.Turn * t;
                        point = before + signedRadius * (corner.Incoming * Mathf.Sin(angle) +
                            normal * (1f - Mathf.Cos(angle)));
                        var tangent = corner.Incoming * Mathf.Cos(angle) + normal * Mathf.Sin(angle);
                        offset = LeftOf(tangent) + Vector2.Lerp(
                            beforeOffset - normal, afterOffset - LeftOf(corner.Outgoing), t);
                    }
                    else
                    {
                        point = Quadratic(before, path.Points[index], after, t);
                        offset = Quadratic(beforeOffset, path.Offsets[index], afterOffset, t);
                    }
                }

                point += path.Start.TangentCorrection * BlendDistance(startBlend, distance);
                point += path.Finish.TangentCorrection * BlendDistance(finishBlend, path.Length - distance);
                float width = path.Width;
                foreach (var blend in path.WidthBlends)
                {
                    float distanceFromEnd = blend.AtStart ? distance : path.Length - distance;
                    width += (blend.TargetWidth - path.Width) * blend.Weight(distanceFromEnd);
                }
                sections.Add(new Section { Point = point, Offset = offset, HalfWidth = width });
            }
            return sections;
        }

        private static float BlendDistance(float span, float distance)
        {
            if (span <= 0f || distance >= span) return 0f;
            float remaining = 1f - distance / span;
            return distance * remaining * remaining;
        }

        private static void Interpolate(RoadPath path, int segment, float distance,
            out Vector2 point, out Vector2 offset)
        {
            float t = Mathf.InverseLerp(path.Stations[segment], path.Stations[segment + 1], distance);
            point = Vector2.Lerp(path.Points[segment], path.Points[segment + 1], t);
            var normal = LeftOf((path.Points[segment + 1] - path.Points[segment]).normalized);
            var start = path.Offsets[segment];
            var finish = path.Offsets[segment + 1];
            offset = Vector2.Lerp(start == Vector2.zero ? normal : start,
                finish == Vector2.zero ? normal : finish, t);
        }

        private static Vector2 Quadratic(Vector2 a, Vector2 b, Vector2 c, float t) =>
            Vector2.Lerp(Vector2.Lerp(a, b, t), Vector2.Lerp(b, c, t), t);

        private static Vector2 LeftOf(Vector2 direction) => new Vector2(-direction.y, direction.x);

        private static Vector2 Mitre(Vector2 incoming, Vector2 outgoing)
        {
            var normal = LeftOf(incoming + outgoing).normalized;
            var side = LeftOf(outgoing);
            if (normal.sqrMagnitude < 0.001f) return side;
            return normal * Mathf.Min(MaxMitre, 1f / Mathf.Max(Vector2.Dot(normal, side), 0.001f));
        }

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
    }
}
