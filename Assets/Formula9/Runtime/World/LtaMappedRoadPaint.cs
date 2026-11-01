using System;
using System.Collections.Generic;
using System.IO;
using Formula9.Backend;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Formula9.World
{
    /// <summary>
    /// SPECIAL LTA features ONLY. Standard lane separators, the two-way
    /// centreline and road edges come from CityRoadTexture mapped onto RoadMesh.
    /// No guessed line joining, OSM lane-divider fallback or yellow-box creation.
    /// All coordinates originate in the unchanged backend fixture JSON files.
    /// </summary>
    public static class LtaMappedRoadPaint
    {
        private const int GroundLevel = 2;
        private const float ChunkMetres = 450f;
        private const float ArrowY = RoadMesh.SurfaceY + .095f;
        private const float CrossingY = RoadMesh.SurfaceY + .155f;
        private const float YellowY = RoadMesh.SurfaceY + .12f;
        private const float StopY = RoadMesh.SurfaceY + .115f;
        private const string ResourcesFolder = "Formula9/RoadData/";

        public enum PaintColour { White, Yellow, Red }
        public sealed class PaintedMesh
        {
            public Mesh Mesh;
            public PaintColour Colour;
        }
        public sealed class Result
        {
            public readonly List<PaintedMesh> Meshes = new List<PaintedMesh>();
            public readonly List<Vector2> CrossingCenters = new List<Vector2>();
            public readonly List<Vector2> SignalJunctionCenters = new List<Vector2>();
            public int GroundLaneFeatures;
            public int GroundArrowFeatures;
            public int YellowBoxLineFeatures;
            public int ZebraCrossings;
            public int SignalCrossings;
            public int ZebraStripeQuads;
            public int SignalBoundarySegments;
            public int GroundStopLineFeatures;
            public int SkippedElevatedOrDepressed;
            public int SkippedUnknownCodes;
            public int SignalHeadsInData;
            // Retained for callers relying on the former diagnostic interface.
            public int MicroGapsJoined;
            public int OsmFallbackSegments;
            public int OsmFallbackEdgeSegments;
            public int MappedCrossingsOffRoad;
            public int MappedCoverageSegments;
            public int InferredSignalStopBars;
            public int SkippedStopBarsWithMappedPaint;
            public int SkippedUnmatchedStopBars;
        }

        private sealed class Batch
        {
            public readonly List<Vector3> Vertices = new List<Vector3>();
            public readonly List<int> Triangles = new List<int>();
            public void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float height)
            {
                // Orient every polygon to face upward in Unity's X/Z plane.
                float signedUp = (b.y - a.y) * (c.x - a.x) - (b.x - a.x) * (c.y - a.y);
                if (Mathf.Abs(signedUp) < .00001f) return;
                int start = Vertices.Count;
                if (signedUp > 0f)
                {
                    Vertices.Add(new Vector3(a.x, height, a.y));
                    Vertices.Add(new Vector3(b.x, height, b.y));
                    Vertices.Add(new Vector3(c.x, height, c.y));
                    Vertices.Add(new Vector3(d.x, height, d.y));
                }
                else
                {
                    Vertices.Add(new Vector3(a.x, height, a.y));
                    Vertices.Add(new Vector3(d.x, height, d.y));
                    Vertices.Add(new Vector3(c.x, height, c.y));
                    Vertices.Add(new Vector3(b.x, height, b.y));
                }
                Triangles.Add(start); Triangles.Add(start + 1); Triangles.Add(start + 2);
                Triangles.Add(start); Triangles.Add(start + 2); Triangles.Add(start + 3);
            }
            public void Triangle(Vector2 a, Vector2 b, Vector2 c, float height)
            {
                float signedUp = (b.y - a.y) * (c.x - a.x) - (b.x - a.x) * (c.y - a.y);
                if (Mathf.Abs(signedUp) < .00001f) return;
                int start = Vertices.Count;
                Vertices.Add(new Vector3(a.x, height, a.y));
                if (signedUp > 0f)
                {
                    Vertices.Add(new Vector3(b.x, height, b.y));
                    Vertices.Add(new Vector3(c.x, height, c.y));
                }
                else
                {
                    Vertices.Add(new Vector3(c.x, height, c.y));
                    Vertices.Add(new Vector3(b.x, height, b.y));
                }
                Triangles.Add(start); Triangles.Add(start + 1); Triangles.Add(start + 2);
            }
            public Mesh MakeMesh(string title)
            {
                var mesh = new Mesh { name = title, indexFormat = IndexFormat.UInt32 };
                mesh.SetVertices(Vertices);
                mesh.SetTriangles(Triangles, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                return mesh;
            }
        }
        private sealed class Painter
        {
            private readonly Dictionary<(int X, int Y, PaintColour C), Batch> batches =
                new Dictionary<(int X, int Y, PaintColour C), Batch>();
            private Batch At(Vector2 center, PaintColour c)
            {
                var id = (Mathf.FloorToInt(center.x / ChunkMetres),
                    Mathf.FloorToInt(center.y / ChunkMetres), c);
                if (!batches.TryGetValue(id, out var batch))
                    batches.Add(id, batch = new Batch());
                return batch;
            }
            public void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d,
                PaintColour colour, float y) => At((a + b + c + d) * .25f, colour).Quad(a,b,c,d,y);
            public void Triangle(Vector2 a, Vector2 b, Vector2 c,
                PaintColour colour, float y) => At((a + b + c) / 3f, colour).Triangle(a,b,c,y);
            public void Stroke(Vector2 from, Vector2 to, float width,
                PaintColour colour, float y)
            {
                var delta = to - from;
                if (delta.sqrMagnitude < .000625f) return;
                var n = new Vector2(-delta.y, delta.x).normalized * width * .5f;
                Quad(from+n, from-n, to-n, to+n, colour, y);
            }
            public void Path(List<Vector2> points, float width, PaintColour colour,
                float y, float dash = 0f, float gap = 0f)
            {
                float distance = 0;
                for (int i = 1; i < points.Count; i++)
                {
                    Vector2 a = points[i-1], b = points[i];
                    var delta = b - a;
                    float length = delta.magnitude;
                    if (length < .025f) continue;
                    if (dash <= 0 || gap <= 0) Stroke(a,b,width,colour,y);
                    else
                    {
                        var direction = delta / length;
                        float cursor = 0;
                        while (cursor < length - .001f)
                        {
                            float phase = (distance + cursor) % (dash + gap);
                            bool visible = phase < dash;
                            float run = visible ? dash - phase : dash + gap - phase;
                            run = Mathf.Min(length - cursor, Mathf.Max(.01f, run));
                            if (visible && run >= .025f)
                                Stroke(a + direction * cursor,
                                    a + direction * (cursor + run), width, colour, y);
                            cursor += run;
                        }
                    }
                    distance += length;
                }
            }
            // Solid, road-style arrows with broad filled heads (rather than thin
            // V-shaped strokes). Position, bearing and type are mapped from LTA;
            // glyph dimensions are an illustrative texture-like rendering.
            public void Arrow(Vector2 origin, float headingDeg, string code)
            {
                float angle = headingDeg * Mathf.Deg2Rad;
                Vector2 forward = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                Vector2 left = new Vector2(-forward.y, forward.x);
                Vector2 P(float sideways, float ahead) =>
                    origin + left * sideways + forward * ahead;

                void Shaft(Vector2 a, Vector2 b, float width = .28f) =>
                    Stroke(a, b, width, PaintColour.White, ArrowY);

                void Head(Vector2 tip, Vector2 direction, float halfWidth = .48f)
                {
                    direction.Normalize();
                    Vector2 lateral = new Vector2(-direction.y, direction.x);
                    Vector2 baseAt = tip - direction * 1.05f;
                    Triangle(tip, baseAt + lateral * halfWidth,
                        baseAt - lateral * halfWidth, PaintColour.White, ArrowY);
                }

                void Trunk() => Shaft(P(0f, -1.8f), P(0f, .65f));
                void Straight()
                {
                    Trunk();
                    Head(P(0f, 2.0f), forward, .52f);
                }
                void SideTurn(bool leftTurn, bool trunk)
                {
                    float side = leftTurn ? 1f : -1f;
                    if (trunk) Shaft(P(0f, -1.8f), P(0f, .18f));
                    Shaft(P(0f, .12f), P(side * .30f, .52f), .24f);
                    Shaft(P(side * .30f, .52f), P(side * .91f, .66f), .24f);
                    Head(P(side * 1.85f, .69f), left * side, .40f);
                }

                switch (code)
                {
                    case "A": SideTurn(false, true); break;   // right
                    case "B": Straight(); SideTurn(true, false); break;
                    case "C": Straight(); SideTurn(false, false); break;
                    case "D": Straight(); break;
                    case "E": SideTurn(true, true); break;    // left
                    case "F": SideTurn(true, true); SideTurn(false, false); break;
                    case "I": Straight(); SideTurn(true, false); SideTurn(false, false); break;
                    // G/H are special merge arrows. No guessed glyph until
                    // their exact mapped geometry is supplied.
                }
            }
            public void Export(Result result)
            {
                foreach (var pair in batches)
                {
                    if (pair.Value.Triangles.Count == 0) continue;
                    result.Meshes.Add(new PaintedMesh
                    {
                        Colour = pair.Key.C,
                        Mesh = pair.Value.MakeMesh("LTA special paint " + pair.Key.C +
                            " " + pair.Key.X + "," + pair.Key.Y)
                    });
                }
            }
        }

        private static JToken TryLoad(string name)
        {
            // Editor: Application.dataPath = Formula9/Assets. JSON stays in backend.
            string file = Path.GetFullPath(Path.Combine(Application.dataPath,
                "..", "backend", "fixtures", "traffic", name + ".json"));
            if (File.Exists(file))
            {
                Debug.Log("[LtaMappedRoadPaint] Loaded backend fixture: " + file);
                return JToken.Parse(File.ReadAllText(file));
            }
            var resource = Resources.Load<TextAsset>(ResourcesFolder + name);
            if (resource == null) return null;
            Debug.Log("[LtaMappedRoadPaint] Loaded Resources fallback: " + name);
            return JToken.Parse(resource.text);
        }
        private static JToken Load(string name) => TryLoad(name) ??
            throw new FileNotFoundException("Missing " + name + ".json. Expected " +
                "backend/fixtures/traffic/ or Resources/" + ResourcesFolder);
        private static List<Vector2> Points(JToken input)
        {
            var output = new List<Vector2>();
            if (!(input is JArray array)) return output;
            foreach (var coordinate in array)
            {
                if (!(coordinate is JArray pair) || pair.Count < 2) continue;
                float x = (float)pair[0], y = (float)pair[1];
                if (float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y)) continue;
                output.Add(new Vector2(x,y));
            }
            return output;
        }
        // A mapped type-M line is used as a transverse stop bar only if it is
        // near a road and broadly perpendicular to its local tangent.
        // This avoids treating long ordinary lines as stop bars.
        private static bool IsTransverse(List<Vector2> line, List<CityRoad> roads)
        {
            if (line.Count < 2 || roads == null || roads.Count == 0) return false;
            var lineDirection = (line[line.Count - 1] - line[0]).normalized;
            var midpoint = (line[0] + line[line.Count - 1]) * .5f;
            float best = float.MaxValue, alignment = 1f;
            foreach (var road in roads)
            {
                Vector2? previous = null;
                foreach (var (x,y) in road.Points)
                {
                    var b = new Vector2((float)x,(float)y);
                    if (previous.HasValue)
                    {
                        var a = previous.Value;
                        var delta = b - a;
                        float length2 = delta.sqrMagnitude;
                        if (length2 > .04f)
                        {
                            var projected = a + delta * Mathf.Clamp01(Vector2.Dot(midpoint-a,delta)/length2);
                            float distance2 = (projected-midpoint).sqrMagnitude;
                            if (distance2 < best)
                            {
                                best = distance2;
                                alignment = Mathf.Abs(Vector2.Dot(delta.normalized,lineDirection));
                            }
                        }
                    }
                    previous = b;
                }
            }
            return best < 12f*12f && alignment < .40f;
        }

        public static Result Build(IEnumerable<CityRoad> roads = null)
        {
            var roadList = roads == null ? null : new List<CityRoad>(roads);
            var markings = Load("toa-payoh-markings");
            var crossings = Load("toa-payoh-crossings");
            if ((string)markings["area"]?["key"] != "toa-payoh" ||
                (string)crossings["area"]?["key"] != "toa-payoh")
                throw new InvalidOperationException("Incorrect LTA fixture area");
            if (!(markings["lane_lines"] is JArray lines) ||
                !(crossings["crossings"] is JArray crossingsArray))
                throw new InvalidOperationException("Missing LTA line or crossing arrays");
            var result = new Result();
            var painter = new Painter();

            foreach (var item in lines)
            {
                if ((int?)item["level"] != GroundLevel)
                {
                    result.SkippedElevatedOrDepressed++;
                    continue;
                }
                var code = (string)item["type_code"];
                // Important: F/I/B/J/etc. longitudinal lane geometry must NOT
                // compete with RoadMesh.BuildBaseLanePaint. Nor are their endpoints
                // automatically connected to other mapped fragments.
                if (code != "N" && code != "M")
                {
                    result.SkippedUnknownCodes++;
                    continue;
                }
                var path = Points(item["points_m"]);
                if (path.Count < 2) continue;
                if (code == "N")
                {
                    painter.Path(path, .34f, PaintColour.Yellow, YellowY);
                    result.YellowBoxLineFeatures++;
                }
                else if (IsTransverse(path,roadList))
                {
                    painter.Path(path, .30f, PaintColour.White, StopY);
                    result.GroundStopLineFeatures++;
                }
                result.GroundLaneFeatures++;
            }
            if (markings["arrows"] is JArray arrows)
            {
                foreach (var item in arrows)
                {
                    if ((int?)item["level"] != GroundLevel) continue;
                    string code = (string)item["type_code"];
                    if (string.IsNullOrEmpty(code) || code.Length != 1 ||
                        "ABCDEFI".IndexOf(code, StringComparison.Ordinal) < 0) continue;
                    float? x = (float?)item["x_m"];
                    float? y = (float?)item["y_m"];
                    float? heading = (float?)item["heading_deg"];
                    if (!x.HasValue || !y.HasValue || !heading.HasValue) continue;
                    painter.Arrow(new Vector2(x.Value, y.Value),heading.Value,code);
                    result.GroundArrowFeatures++;
                }
            }
            foreach (var item in crossingsArray)
            {
                var axis = Points(item["line_m"]);
                if (axis.Count >= 2)
                    result.CrossingCenters.Add((axis[0]+axis[axis.Count-1])*.5f);
                string kind = (string)item["render_as"];
                if (kind == "zebra" && item["stripes_m"] is JArray stripes)
                {
                    foreach (var stripe in stripes)
                    {
                        var polygon = Points(stripe);
                        if (polygon.Count != 4) continue;
                        painter.Quad(polygon[0],polygon[1],polygon[2],polygon[3],
                            PaintColour.White,CrossingY);
                        result.ZebraStripeQuads++;
                    }
                    result.ZebraCrossings++;
                }
                else if (kind == "signal_crossing" && item["edges_m"] is JArray edges)
                {
                    foreach (var edge in edges)
                    {
                        var line = Points(edge);
                        if (line.Count < 2) continue;
                        painter.Path(line,.44f,PaintColour.White,CrossingY,1.20f,.55f);
                        result.SignalBoundarySegments += line.Count-1;
                    }
                    result.SignalCrossings++;
                }
            }
            var signals = TryLoad("toa-payoh-signals");
            if (signals != null)
            {
                result.SignalHeadsInData = (signals["signals"] as JArray)?.Count ?? 0;
                if (signals["junctions"] is JArray junctions)
                {
                    foreach (var junction in junctions)
                    {
                        float? x = (float?)junction["x_m"];
                        float? y = (float?)junction["y_m"];
                        if (x.HasValue && y.HasValue)
                            result.SignalJunctionCenters.Add(new Vector2(x.Value,y.Value));
                    }
                }
            }
            painter.Export(result);
            Debug.Log($"[LtaMappedRoadPaint] Special features only: {result.YellowBoxLineFeatures} " +
                $"mapped yellow-box paths, {result.GroundStopLineFeatures} transverse mapped type-M lines, " +
                $"{result.GroundArrowFeatures} mapped arrows, {result.ZebraCrossings} zebra " +
                $"({result.ZebraStripeQuads} stripes), {result.SignalCrossings} signal crossings " +
                $"({result.SignalBoundarySegments} edges), {result.Meshes.Count} meshes; " +
                "no longitudinal lane duplicates or guessed box locations.");
            return result;
        }
    }
}
