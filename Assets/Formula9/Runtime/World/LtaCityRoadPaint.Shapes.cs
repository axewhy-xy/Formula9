using System.Collections.Generic;
using UnityEngine;

namespace Formula9.World
{
    public sealed partial class LtaCityRoadPaint
    {
        public sealed partial class PaintMeshBuilder
        {
            public void DrawLine(Vector2 start, Vector2 end, float width, PaintColour colour)
            {
                if ((end - start).sqrMagnitude <= 1e-8f || width <= 0)
                    return;
                Vector2 side = Normal(end - start);
                AddStroke(start, end, side, side, width, 0f, colour);
            }

            public void DrawPath(IReadOnlyList<Vector2> points, float width, PaintColour colour,
                float dash = 0f, float gap = 0f, float separation = 0f, bool avoidCrossings = false)
            {
                if (points.Count < 2 || width <= 0f)
                    return;

                float distance = 0f;
                for (int i = 1; i < points.Count; i++)
                {
                    Vector2 a = points[i - 1], b = points[i];
                    float length = Vector2.Distance(a, b);
                    if (length <= 1e-5f)
                        continue;
                    Vector2 sideA = JoinNormal(points, i - 1), sideB = JoinNormal(points, i);

                    if (dash <= 0f || gap <= 0f)
                        Emit(a, b, sideA, sideB);
                    else
                    {
                        float period = dash + gap;
                        float end = distance + length;
                        for (int cycle = Mathf.FloorToInt(distance / period); cycle * period < end; cycle++)
                        {
                            float first = Mathf.Max(distance, cycle * period);
                            float last = Mathf.Min(end, cycle * period + dash);
                            if (last - first <= 1e-5f)
                                continue;
                            float t0 = (first - distance) / length, t1 = (last - distance) / length;
                            Emit(Vector2.Lerp(a, b, t0), Vector2.Lerp(a, b, t1),
                                Vector2.Lerp(sideA, sideB, t0), Vector2.Lerp(sideA, sideB, t1));
                        }
                    }
                    distance += length;
                }

                void Emit(Vector2 a, Vector2 b, Vector2 sideA, Vector2 sideB)
                {
                    float offset = separation / 2f;
                    AddStroke(a, b, sideA, sideB, width, offset, colour, avoidCrossings);
                    if (offset > 0f)
                        AddStroke(a, b, sideA, sideB, width, -offset, colour, avoidCrossings);
                }
            }

            public void DrawZigzag(IReadOnlyList<Vector2> guide, float width, float spacing,
                float amplitude, PaintColour colour, bool avoidCrossings = false)
            {
                if (guide.Count < 2 || spacing <= 0f)
                    return;

                var station = new float[guide.Count];
                for (int i = 1; i < guide.Count; i++)
                    station[i] = station[i - 1] + Vector2.Distance(guide[i - 1], guide[i]);
                float total = station[station.Length - 1];
                if (total <= 1e-5f)
                    return;
                int count = Mathf.Max(2, Mathf.RoundToInt(total / spacing));
                var path = new List<Vector2>(count + 1) { guide[0] };
                int segment = 1;
                for (int i = 1; i < count; i++)
                {
                    float distance = total * i / count;
                    while (station[segment] <= distance)
                        segment++;
                    Vector2 a = guide[segment - 1], b = guide[segment];
                    float t = (distance - station[segment - 1]) / (station[segment] - station[segment - 1]);
                    path.Add(Vector2.Lerp(a, b, t) + Normal(b - a) * (i % 2 == 0 ? amplitude : -amplitude));
                }
                path.Add(guide[guide.Count - 1]);
                DrawPath(path, width, colour, avoidCrossings: avoidCrossings);
            }

            public void DrawArrow(Vector2 anchor, float headingDegrees, IReadOnlyList<Vector2[]> branches,
                PaintColour colour, bool centreFullShape = false)
            {
                float radians = headingDegrees * Mathf.Deg2Rad;
                var forward = new Vector2(Mathf.Cos(radians), Mathf.Sin(radians));
                var right = new Vector2(forward.y, -forward.x);
                var shapes = new List<Vector2[]>();
                foreach (Vector2[] branch in branches)
                {
                    if (branch.Length < 2)
                        continue;
                    Vector2 tip = branch[branch.Length - 1];
                    Vector2 heading = (tip - branch[branch.Length - 2]).normalized;
                    if (heading.sqrMagnitude == 0)
                        continue;
                    Vector2 headBase = tip - heading * 0.85f;
                    Vector2 wing = Normal(heading) * 0.50f;
                    Vector2 shaftEnd = headBase + heading * 0.05f;
                    var shaft = new List<Vector2>();
                    for (int i = 0; i < branch.Length - 1; i++)
                        if (Vector2.Dot(branch[i] - shaftEnd, heading) < 0)
                            shaft.Add(Place(branch[i]));
                    shaft.Add(Place(shaftEnd));
                    for (int i = 1; i < shaft.Count; i++)
                        shapes.Add(StrokeQuad(shaft[i - 1], shaft[i],
                            JoinNormal(shaft, i - 1), JoinNormal(shaft, i), 0.24f, 0f));
                    shapes.Add(new[] { Place(tip), Place(headBase + wing), Place(headBase - wing) });
                }

                Vector2 offset = Vector2.zero;
                if (centreFullShape && shapes.Count > 0)
                {
                    Vector2 candidate = ArrowCenterOffset(anchor, forward, right, shapes);
                    if (candidate.sqrMagnitude > 0f && FitsAsphalt(shapes, candidate))
                        offset = candidate;
                }
                foreach (Vector2[] shape in shapes)
                {
                    for (int i = 0; i < shape.Length; i++)
                        shape[i] += offset;
                    DrawConvex(shape, colour);
                }

                Vector2 Place(Vector2 p) => anchor + p.x * right + p.y * forward;
            }

            private Vector2 ArrowCenterOffset(Vector2 anchor, Vector2 forward, Vector2 right,
                List<Vector2[]> shapes)
            {
                float minShape = float.PositiveInfinity, maxShape = float.NegativeInfinity;
                foreach (Vector2[] shape in shapes)
                    foreach (Vector2 point in shape)
                    {
                        float lateral = Vector2.Dot(point - anchor, right);
                        minShape = Mathf.Min(minShape, lateral);
                        maxShape = Mathf.Max(maxShape, lateral);
                    }

                float insideLeft = -5f, insideRight = 5f;
                float minimumCentre = float.PositiveInfinity, maximumCentre = float.NegativeInfinity;
                float centreSum = 0f;
                for (int station = 0; station < 3; station++)
                {
                    Vector2 across = anchor + forward * (station * 0.8f);
                    float left = -5f, rightEdge = 5f;
                    foreach (var edge in laneEdges)
                    {
                        Vector2 delta = edge.B - edge.A;
                        float along = Vector2.Dot(delta, forward);
                        if (Mathf.Abs(along) <= 0.85f * delta.magnitude)
                            continue;

                        Vector2 from = edge.A - across;
                        float t = -Vector2.Dot(from, forward) / along;
                        if (t < 0f || t > 1f)
                            continue;
                        float lateral = Vector2.Dot(from + delta * t, right);
                        if (lateral < 0f)
                            left = Mathf.Max(left, lateral);
                        else
                            rightEdge = Mathf.Min(rightEdge, lateral);
                    }
                    float width = rightEdge - left;
                    if (width < 2.3f || width > 4.6f || Mathf.Min(-left, rightEdge) < 0.60f)
                        return Vector2.zero;

                    float centre = (left + rightEdge) * 0.5f;
                    centreSum += centre;
                    minimumCentre = Mathf.Min(minimumCentre, centre);
                    maximumCentre = Mathf.Max(maximumCentre, centre);
                    insideLeft = Mathf.Max(insideLeft, left);
                    insideRight = Mathf.Min(insideRight, rightEdge);
                }

                float shift = centreSum / 3f - (minShape + maxShape) * 0.5f;
                if (maximumCentre - minimumCentre > 0.30f || Mathf.Abs(shift) < 0.05f ||
                    Mathf.Abs(shift) > 1.10f || minShape + shift < insideLeft + 0.08f ||
                    maxShape + shift > insideRight - 0.08f)
                    return Vector2.zero;
                return right * shift;
            }

            public void DrawZebra(Vector2 start, Vector2 end, float crossingWidth,
                float stripeLength, float gap, PaintColour colour, bool reserveApproachPaint = false)
            {
                Vector2 path = end - start;
                float length = path.magnitude;
                if (float.IsNaN(crossingWidth) || float.IsInfinity(crossingWidth) || crossingWidth <= 0f ||
                    float.IsNaN(stripeLength) || float.IsInfinity(stripeLength) || stripeLength <= 0f ||
                    float.IsNaN(gap) || float.IsInfinity(gap) || gap <= 0f || length < stripeLength)
                    return;

                Vector2 direction = path / length;
                Vector2 side = Normal(direction);
                if (reserveApproachPaint)
                    ReserveCrossing(StrokeQuad(start, end, side, side, crossingWidth, 0f), 0.10f);

                float spacing = stripeLength + gap;
                int count = Mathf.FloorToInt((length + gap) / spacing);
                float used = count * stripeLength + (count - 1) * gap;
                float margin = (length - used) * 0.5f;
                for (int i = 0; i < count; i++)
                {
                    Vector2 a = start + direction * (margin + i * spacing);
                    Vector2 b = a + direction * stripeLength;
                    AddStroke(a, b, side, side, crossingWidth, 0f, colour);
                }
            }

            private void AddStroke(Vector2 a, Vector2 b, Vector2 sideA, Vector2 sideB,
                float width, float offset, PaintColour colour, bool avoidCrossings = false)
            {
                DrawConvex(StrokeQuad(a, b, sideA, sideB, width, offset), colour, avoidCrossings);
            }

            private static Vector2[] StrokeQuad(Vector2 a, Vector2 b, Vector2 sideA, Vector2 sideB,
                float width, float offset)
            {
                float low = offset - width / 2f, high = offset + width / 2f;
                return new[] { a + sideA * low, a + sideA * high, b + sideB * high, b + sideB * low };
            }

            private static Vector2 JoinNormal(IReadOnlyList<Vector2> points, int i)
            {
                Vector2 incoming = Normal(points[Mathf.Max(1, i)] - points[Mathf.Max(0, i - 1)]);
                Vector2 outgoing = Normal(points[Mathf.Min(i + 1, points.Count - 1)] -
                    points[Mathf.Min(i, points.Count - 2)]);
                Vector2 average = (incoming + outgoing).normalized;
                float dot = Vector2.Dot(average, outgoing);
                return dot > 0.5f ? average / dot : outgoing;
            }

            private static Vector2 Normal(Vector2 direction)
            {
                Vector2 unit = direction.normalized;
                return new Vector2(-unit.y, unit.x);
            }
        }
    }
}
