using System.Collections.Generic;
using UnityEngine;

namespace Formula9.World
{
    public static partial class RoadMesh
    {
        private const float MedianStepM = 2f;
        private const float MinMedianGapM = 0.02f;
        private const float ConcreteMedianGapM = 0.45f;
        private const float MedianEdgeOverlapM = 0.08f;
        private const float MaxMedianGapM = 8f;
        private const float PlantingGapM = 2.5f;

        private struct MedianStrip
        {
            public Vector2 A, B, C, D;
            public float Width;
        }

        private struct MedianProbe
        {
            public RoadPath Opposite;
            public Vector2 InnerEdge, OppositeEdge;
            public float Gap;
        }

        private struct RoadSpan
        {
            public RoadPath Path;
            public int Index;
            public float Start, Finish;
            public Vector2 A => Path.Sections[Index - 1].Point;
            public Vector2 B => Path.Sections[Index].Point;
            public Section At(float t)
            {
                var a = Path.Sections[Index - 1];
                var b = Path.Sections[Index];
                return new Section
                {
                    Point = Vector2.Lerp(a.Point, b.Point, t),
                    Offset = Vector2.Lerp(a.Offset, b.Offset, t),
                    HalfWidth = Mathf.Lerp(a.HalfWidth, b.HalfWidth, t)
                };
            }
        }

        private static IEnumerable<MedianStrip> FindMedianStrips(List<RoadPath> paths)
        {
            var edges = new Dictionary<Vector2Int, List<RoadSpan>>();
            var order = new Dictionary<RoadPath, int>();
            foreach (var path in paths)
            {
                order.Add(path, order.Count);
                for (int i = 1; i < path.Sections.Count; i++)
                {
                    var span = new RoadSpan
                    {
                        Path = path, Index = i,
                        Start = path.PaintDistances[i - 1], Finish = path.PaintDistances[i]
                    };
                    if (span.Finish - span.Start < MinSegmentM) continue;
                    float padding = 1f + Mathf.Max(span.At(0f).HalfWidth * span.At(0f).Offset.magnitude,
                        span.At(1f).HalfWidth * span.At(1f).Offset.magnitude);
                    var margin = Vector2.one * (padding + MaxMedianGapM);
                    var min = OverlapCell(Vector2.Min(span.A, span.B) - margin);
                    var max = OverlapCell(Vector2.Max(span.A, span.B) + margin);
                    for (int x = min.x; x <= max.x; x++)
                    for (int y = min.y; y <= max.y; y++)
                    {
                        var cell = new Vector2Int(x, y);
                        if (!edges.TryGetValue(cell, out var list)) edges[cell] = list = new List<RoadSpan>();
                        list.Add(span);
                    }
                }
            }

            foreach (var path in paths)
            {
                if (!CanHaveMedian(path)) continue;
                float from = MedianEndClearance(path.Start, path);
                float to = path.PaintLength - MedianEndClearance(path.Finish, path);
                if (to - from < 4f * MedianStepM) continue;

                int steps = Mathf.CeilToInt((to - from) / MedianStepM);
                var probes = new MedianProbe[steps + 1];
                int index = 1;
                for (int i = 0; i <= steps; i++)
                {
                    float station = Mathf.Lerp(from, to, (float)i / steps);
                    while (index < path.Sections.Count - 1 && path.PaintDistances[index] < station) index++;
                    var span = new RoadSpan
                    {
                        Path = path, Index = index,
                        Start = path.PaintDistances[index - 1], Finish = path.PaintDistances[index]
                    };
                    probes[i] = ProbeMedian(span, station, edges);
                }

                int first = 0;
                while (first < steps)
                {
                    var other = probes[first].Opposite;
                    if (other == null || order[other] <= order[path])
                    {
                        first++;
                        continue;
                    }

                    int last = first;
                    while (last < steps && probes[last + 1].Opposite == other &&
                        Mathf.Abs(probes[last + 1].Gap - probes[last].Gap) < 1.25f &&
                        MedianGapClear(probes[last], probes[last + 1], path, other, edges)) last++;

                    // Require a sustained gap. An isolated projection onto a slip lane
                    // or across a junction is insufficient to build a raised median.
                    if (last - first >= 4)
                    {
                        float average = 0f;
                        for (int i = first; i <= last; i++) average += probes[i].Gap;
                        average /= last - first + 1;
                        for (int i = first; i < last; i++)
                            yield return new MedianStrip
                            {
                                A = probes[i].InnerEdge, B = probes[i].OppositeEdge,
                                C = probes[i + 1].InnerEdge, D = probes[i + 1].OppositeEdge,
                                Width = average
                            };
                    }
                    first = last + 1;
                }
            }
        }

        private static bool CanHaveMedian(RoadPath path)
        {
            string roadClass = path.Road.Class ?? string.Empty;
            return path.Road.Oneway && !IsUnmarkedClass(roadClass) &&
                !roadClass.EndsWith("_link", System.StringComparison.OrdinalIgnoreCase);
        }

        private static float MedianEndClearance(End end, RoadPath path) =>
            end.Joined && !end.JunctionPaintBreak ? 0f :
                Mathf.Max(path.Width, end.IntersectionClearance) + 2f;

        private static MedianProbe ProbeMedian(RoadSpan source, float station,
            Dictionary<Vector2Int, List<RoadSpan>> edges)
        {
            var result = default(MedianProbe);
            var section = source.At(Mathf.InverseLerp(source.Start, source.Finish, station));
            var inner = section.Point - section.Offset * section.HalfWidth;
            var inward = -section.Offset.normalized;
            var forward = (source.B - source.A).normalized;
            if (!edges.TryGetValue(OverlapCell(inner), out var nearby)) return result;

            float best = MaxMedianGapM;
            foreach (var candidate in nearby)
            {
                if (candidate.Path == source.Path || !CanHaveMedian(candidate.Path)) continue;
                var tangent = candidate.B - candidate.A;
                if (Vector2.Dot(forward, tangent.normalized) > -.94f) continue;
                var a = candidate.At(0f);
                var b = candidate.At(1f);
                var p = a.Point - a.Offset * a.HalfWidth;
                var q = b.Point - b.Offset * b.HalfWidth;
                var edge = q - p;
                float denominator = inward.x * edge.y - inward.y * edge.x;
                if (Mathf.Abs(denominator) < MinSegmentM) continue;
                var delta = p - inner;
                float gap = (delta.x * edge.y - delta.y * edge.x) / denominator;
                float t = (delta.x * inward.y - delta.y * inward.x) / denominator;
                if (gap < MinMedianGapM || gap >= best || t < 0f || t > 1f) continue;

                float otherStation = Mathf.Lerp(candidate.Start, candidate.Finish, t);
                if (otherStation < MedianEndClearance(candidate.Path.Start, candidate.Path) ||
                    candidate.Path.PaintLength - otherStation < MedianEndClearance(candidate.Path.Finish, candidate.Path))
                    continue;

                // Cover the join with a small overlap under both paved edges.
                result = new MedianProbe
                {
                    Opposite = candidate.Path,
                    InnerEdge = inner - inward * MedianEdgeOverlapM,
                    OppositeEdge = inner + inward * (gap + MedianEdgeOverlapM),
                    Gap = gap
                };
                best = gap;
            }
            return result;
        }

        private static bool MedianGapClear(MedianProbe a, MedianProbe b, RoadPath source, RoadPath other,
            Dictionary<Vector2Int, List<RoadSpan>> edges)
        {
            var centre = (a.InnerEdge + a.OppositeEdge + b.InnerEdge + b.OppositeEdge) * .25f;
            if (!edges.TryGetValue(OverlapCell(centre), out var spans)) return true;
            foreach (var span in spans)
            {
                if (span.Path == source || span.Path == other || IsUnmarkedClass(span.Path.Road.Class)) continue;
                var delta = span.B - span.A;
                float t = Mathf.Clamp01(Vector2.Dot(centre - span.A, delta) / delta.sqrMagnitude);
                var section = span.At(t);
                float radius = section.HalfWidth * section.Offset.magnitude +
                    Mathf.Max(a.Gap, b.Gap) * .5f + 1f;
                if ((centre - section.Point).sqrMagnitude < radius * radius) return false;
            }
            return true;
        }
    }
}
