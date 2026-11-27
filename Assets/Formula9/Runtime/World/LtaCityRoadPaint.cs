using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Formula9.World
{
    public sealed partial class LtaCityRoadPaint : MonoBehaviour
    {
        internal bool Build(JObject tile, Material whitePaint, Material yellowPaint, float halfTileM, PaintMeshBuilder builder, Action<Mesh> ownMesh)
        {
            try
            {
                var data = tile["markings"]?.ToObject<MarkingData>();
                if (data?.Lines == null) return false;
                AddCrossingPaint(builder, tile["crossings"]?.ToObject<CrossingData>());
                Dictionary<string, List<Feature>> lineGroups = GroupByType(data.Lines);
                foreach (var group in lineGroups)
                {
                    builder.CompactPaintEnvelope = group.Key == "SURVEY_WORD" || group.Key == "SURVEY_FILL";
                    bool drawMarking = Patterns.TryGetValue(group.Key, out Pattern pattern) || group.Key == "N";
                    bool laneBoundary = LaneBoundaryTypes.Contains(group.Key);
                    bool avoidCrossings = CrossingApproachTypes.Contains(group.Key);
                    if (!drawMarking && !laneBoundary) continue;

                    var drawnPaths = new HashSet<Vector2[]>(PathComparer.Instance);
                    foreach (Feature item in group.Value)
                    {
                        Vector2[] path = ReadPath(item.Points);
                        if (path.Length < 2 || !builder.IntersectsBounds(path, 0.5f) || !drawnPaths.Add(path))
                            continue;

                        if (laneBoundary)
                            builder.AddLaneBoundary(path);
                        if (!drawMarking)
                            continue;

                        if (group.Key == "S")
                            builder.AlignBusBay(path);

                        if (group.Key == "N")
                        {
                            builder.DrawPath(path, 0.20f, PaintColour.Yellow);
                        }
                        else if (pattern.Zigzag)
                            builder.DrawZigzag(path, pattern.Width, 1f, 0.25f, pattern.Colour, avoidCrossings);
                        else if (path.Length == 2 && pattern.Dash == 0f && pattern.Separation == 0f)
                            builder.DrawLine(path[0], path[1], pattern.Width, pattern.Colour);
                        else
                            builder.DrawPath(path, pattern.Width, pattern.Colour, pattern.Dash,
                                pattern.Gap, pattern.Separation, avoidCrossings);
                    }
                }

                builder.CompactPaintEnvelope = false;
                foreach (var group in GroupByType(builder.DrawArrowsAsSymbols ? Array.Empty<Feature>() : data.Arrows))
                {
                    if (!ArrowPaths.TryGetValue(group.Key, out Vector2[][] paths))
                    {
                        continue;
                    }
                    foreach (Feature item in group.Value)
                    {
                        if (!item.X.HasValue || !item.Z.HasValue || !item.Heading.HasValue)
                            continue;
                        Vector2 origin = ToLocal(item.X.Value, item.Z.Value);
                        const float arrowReach = 6f;
                        Rect bounds = builder.Bounds;
                        bounds = Rect.MinMaxRect(bounds.xMin - arrowReach, bounds.yMin - arrowReach,
                            bounds.xMax + arrowReach, bounds.yMax + arrowReach);
                        bool canCentre = Mathf.Max(Mathf.Abs(origin.x), Mathf.Abs(origin.y)) <
                            halfTileM - arrowReach;
                        if (bounds.Contains(origin))
                            builder.DrawArrow(origin, item.Heading.Value, paths, PaintColour.White,
                                centreFullShape: canCentre && group.Key != "D");
                    }
                }

                if (!builder.HasPaint) return false;
                Mesh generatedMesh = builder.CreateMesh(ownMesh);
                var generatedObject = new GameObject("LTA procedural paint", typeof(MeshFilter), typeof(MeshRenderer));
                generatedObject.transform.SetParent(transform, false);
                generatedObject.GetComponent<MeshFilter>().sharedMesh = generatedMesh;
                var visual = generatedObject.GetComponent<MeshRenderer>();
                visual.sharedMaterials = new[] { whitePaint, yellowPaint };
                visual.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                visual.receiveShadows = false;
                return true;
            }
            catch (Exception error) when (error is JsonException || error is InvalidOperationException)
            {
                throw new FormatException("Invalid LTA marking tile.", error);
            }
        }

        private void AddCrossingPaint(PaintMeshBuilder builder, CrossingData data)
        {
            if (data?.Crossings == null)
                return;

            ZebraDimensions zebra = data.Dimensions ?? new ZebraDimensions();
            foreach (Crossing item in data.Crossings)
            {
                if (item == null)
                    continue;

                if (item.Style == "zebra")
                {
                    Vector2[] line = ReadPath(item.Line);
                    if (line.Length >= 2 && builder.IntersectsBounds(line, zebra.Width * 0.5f))
                        builder.DrawZebra(line[0], line[line.Length - 1], zebra.Width, zebra.Stripe, zebra.Gap,
                            PaintColour.White, reserveApproachPaint: true);
                    continue;
                }

                bool bicycle = item.Style == "bicycle_crossing";
                if ((!bicycle && item.Style != "signal_crossing") || item.Edges == null || item.Edges.Length != 2)
                    continue;

                Vector2[] a = ReadPath(item.Edges[0]);
                Vector2[] b = ReadPath(item.Edges[1]);
                if (a.Length != 2 || b.Length != 2)
                    continue;

                Pattern dots = Patterns[bicycle ? "A8" : "A4"];
                var outline = new[] { a[0], a[1], b[1], b[0] };
                if (!builder.IntersectsBounds(outline, dots.Width))
                    continue;

                builder.ReserveCrossing(outline, dots.Width * 0.5f);
                if (item.DrawEdges == null || item.DrawEdges.Length != 2 || item.DrawEdges[0])
                    builder.DrawPath(a, dots.Width, dots.Colour, dots.Dash, dots.Gap);
                if (item.DrawEdges == null || item.DrawEdges.Length != 2 || item.DrawEdges[1])
                    builder.DrawPath(b, dots.Width, dots.Colour, dots.Dash, dots.Gap);
            }
        }

        private static Dictionary<string, List<Feature>> GroupByType(Feature[] source)
        {
            var groups = new Dictionary<string, List<Feature>>(StringComparer.Ordinal);
            foreach (Feature feature in source)
            {
                if (feature == null || feature.Level != 2 || string.IsNullOrWhiteSpace(feature.Type))
                    continue;
                if (!groups.TryGetValue(feature.Type, out List<Feature> records))
                {
                    records = new List<Feature>();
                    groups.Add(feature.Type, records);
                }
                records.Add(feature);
            }
            return groups;
        }

        private Vector2[] ReadPath(double[][] source)
        {
            var points = new List<Vector2>();
            if (source == null)
                return points.ToArray();
            foreach (double[] p in source)
            {
                if (p == null || p.Length < 2 || double.IsNaN(p[0]) || double.IsNaN(p[1]) ||
                    double.IsInfinity(p[0]) || double.IsInfinity(p[1]))
                    return Array.Empty<Vector2>();
                Vector2 point = ToLocal(p[0], p[1]);
                if (points.Count == 0 || (points[points.Count - 1] - point).sqrMagnitude > 1e-8f)
                    points.Add(point);
            }
            return points.ToArray();
        }

        private Vector2 ToLocal(double x, double z)
        {
            Vector3 origin = transform.localPosition;
            var offset = new Vector3((float)(x - origin.x), 0, (float)(z - origin.z));
            Transform parent = transform.parent;
            Vector3 local = transform.InverseTransformVector(parent != null ? parent.TransformVector(offset) : offset);
            return new Vector2(local.x, local.z);
        }

        // Different survey IDs occasionally describe exactly the same line in either direction.
        // Only identical paths within one paint type share an entry. Parallel lines remain separate.
        private sealed class PathComparer : IEqualityComparer<Vector2[]>
        {
            internal static readonly PathComparer Instance = new PathComparer();
            public bool Equals(Vector2[] a, Vector2[] b)
            {
                if (a.Length != b.Length) return false;
                bool forward = true, reverse = true;
                for (int i = 0; i < a.Length && (forward || reverse); i++)
                {
                    forward &= a[i].Equals(b[i]);
                    reverse &= a[i].Equals(b[b.Length - 1 - i]);
                }
                return forward || reverse;
            }

            public int GetHashCode(Vector2[] points)
            {
                unchecked
                {
                    int forward = 17, reverse = 17;
                    for (int i = 0; i < points.Length; i++)
                    {
                        forward = forward * 31 + points[i].GetHashCode();
                        reverse = reverse * 31 + points[points.Length - 1 - i].GetHashCode();
                    }
                    return forward + reverse;
                }
            }
        }

        private sealed class MarkingData
        {
            [JsonProperty("lane_lines")] public Feature[] Lines;
            [JsonProperty("arrows")] public Feature[] Arrows;
        }

        private sealed class CrossingData
        {
            [JsonProperty("crossings")] public Crossing[] Crossings;
            [JsonProperty("crossing_shape")] public ZebraDimensions Dimensions;
        }

        private sealed class Crossing
        {
            [JsonProperty("render_as")] public string Style;
            [JsonProperty("edges_m")] public double[][][] Edges;
            [JsonProperty("draw_edges")] public bool[] DrawEdges;
            [JsonProperty("line_m")] public double[][] Line;
        }

        private sealed class ZebraDimensions
        {
            // Defaults match the assumptions used by the current crossing export.
            [JsonProperty("width_m")] public float Width = 3f;
            [JsonProperty("stripe_thickness_m")] public float Stripe = 0.5f;
            [JsonProperty("stripe_gap_m")] public float Gap = 0.5f;
        }

        private sealed class Feature
        {
            [JsonProperty("type_code")] public string Type;
            [JsonProperty("level")] public float Level;
            [JsonProperty("points_m")] public double[][] Points;
            [JsonProperty("x_m")] public double? X;
            [JsonProperty("y_m")] public double? Z;
            [JsonProperty("heading_deg")] public float? Heading;
        }
    }
}
