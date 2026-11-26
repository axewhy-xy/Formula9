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
        private const float CrossingY = RoadMesh.SurfaceY + .155f;
        private const float YellowY = RoadMesh.SurfaceY + .12f;
        private const float DividerEdgeY = RoadMesh.SurfaceY + .105f;
        private const float StopY = RoadMesh.SurfaceY + .115f;
        private const string ResourcesFolder = "Formula9/RoadData/";

        public enum PaintColour { White, Yellow, Red }
        
        public sealed class Result
        {
           
            public readonly List<Vector2> SignalJunctionCenters = new List<Vector2>();
            internal readonly List<List<Vector2>> StopLines = new List<List<Vector2>>();
            public int GroundLaneFeatures;
            public int GroundArrowFeatures; // Retained for existing diagnostics.
            public int DividerEdgeLineFeatures;
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
        // LTA type J covers stop lines and expressway shoulder lines.
        // Use the nearby road direction to select transverse stop-line candidates.
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

        internal static Result LoadJunctionData(IEnumerable<CityRoad> roads)
        {
            var result = new Result();
            var roadList = new List<CityRoad>(roads);
            var markings = TryLoad("toa-payoh-markings");
            if (markings?["lane_lines"] is JArray lines)
            {
                foreach (var item in lines)
                {
                    if ((int?)item["level"] != GroundLevel || (string)item["type_code"] != "J") continue;
                    var path = Points(item["points_m"]);
                    if (IsTransverse(path, roadList)) result.StopLines.Add(path);
                }
            }

            ReadSignalCentres(result);
            return result;
        }

       
        private static void ReadSignalCentres(Result result)
        {
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
        }
    }
}
