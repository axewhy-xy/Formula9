using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Formula9.World
{
    public sealed partial class CityView
    {
        // The legacy road mesh is the only pavement. The LTA export supplies paint records.
        private async Task<bool> LoadRoadMarkingsAsync(string area, MeshRenderer roadRenderer, Shader shader, CancellationToken cancellation)
        {
            var asset = Resources.Load<TextAsset>($"Formula9/RoadData/{area}-city-map");
            if (asset == null) return false;

            var meshes = new List<Mesh>();
            GameObject holder = null;
            bool committed = false;
            try
            {
                string json = asset.text;
                var supplementalAsset = Resources.Load<TextAsset>($"Formula9/RoadData/{area}-surface-paint");
                string supplementalJson = supplementalAsset != null ? supplementalAsset.text : null;
                var indexed = await Task.Run(() =>
                {
                    var map = JObject.Parse(json);
                    if ((string)map["coordinate_space"] != "city-local-xz-metres" ||
                        !string.Equals((string)map["area"]?["key"], area, StringComparison.OrdinalIgnoreCase) ||
                        !(map["tiles"] is JArray tiles))
                        throw new FormatException("Invalid LTA marking map or service area.");
                    float size = (float?)map["tile_size_m"] ?? 0;
                    if (!(size > 0) || float.IsInfinity(size)) throw new FormatException("Invalid LTA tile size.");
                    if (supplementalJson != null)
                    {
                        var supplemental = JObject.Parse(supplementalJson);
                        var byOrigin = new Dictionary<(float, float), JArray>();
                        foreach (JObject tile in (JArray)supplemental["tiles"])
                            byOrigin.Add(((float)tile["origin_m"][0], (float)tile["origin_m"][1]),
                                (JArray)tile["lines"]);
                        foreach (JObject tile in tiles)
                        {
                            var xy = tile["origin_m"];
                            if (byOrigin.TryGetValue(((float)xy[0], (float)xy[1]), out var additions) &&
                                tile["markings"]?["lane_lines"] is JArray mapped)
                                foreach (var line in additions) mapped.Add(line.DeepClone());
                        }
                    }
                    return (Tiles: tiles, Size: size);
                }, cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (disposed) throw new OperationCanceledException();

                const string materialFolder = "Formula9/LtaRoads/Materials/";
                var coverageShader = Resources.Load<Shader>("Formula9/LtaRoadPaintCoverage");
                bool useCoverage = coverageShader != null && coverageShader.isSupported;

                if (!useCoverage)
                    Debug.LogWarning("[CityView] LtaRoadPaintCoverage shader unavailable; using fixed-width LTA paint.");

                Material Paint(string name, Color colour)
                {
                    var preset = Resources.Load<Material>(materialFolder + name);
                    if (preset != null && preset.HasProperty("_Color")) colour = preset.color;
                    var mat = Own(new Material(useCoverage ? coverageShader : shader)
                        { name = name, color = colour, renderQueue = 2460 });
                    if (!useCoverage && mat.HasProperty("_StencilComp"))
                    {
                        mat.SetInt("_StencilComp", (int)UnityEngine.Rendering.CompareFunction.Always);
                    }
                    if (mat.HasProperty("_ZWrite")) mat.SetInt("_ZWrite", 0);
                    return mat;
                }

                var symbolShader = Resources.Load<Shader>("Formula9/LtaReadableMarkings");
                var symbols = symbolShader != null && symbolShader.isSupported
                    ? Own(new Material(symbolShader) { name = "LTA directional arrow overlay" }) : null;
                var white = Paint("WhitePaint", MarkingColor);
                var yellow = Paint("YellowPaint", new Color(.94f, .73f, .13f));
                holder = new GameObject("LTA paint over original roads");
                holder.transform.SetParent(root, false);
                var originalAsphalt = roadRenderer.sharedMaterials[RoadMesh.AsphaltLayer];
                int built = 0;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                foreach (var record in indexed.Tiles)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (disposed) throw new OperationCanceledException();
                    if (!(record is JObject tile) || !(tile["origin_m"] is JArray origin) || origin.Count != 2)
                        throw new FormatException("Invalid LTA marking tile origin.");
                    var center = new Vector2((float)origin[0], (float)origin[1]);
                    var key = new Vector2Int(Mathf.FloorToInt(center.x / indexed.Size),
                        Mathf.FloorToInt(center.y / indexed.Size));
                    if (!(tile["vertices_m"] is JArray footprint) || !(tile["triangles"] is JArray indices) ||
                        indices.Count % 3 != 0)
                        throw new FormatException("Invalid preview road footprint.");
                    bool hasRoad = indices.Count > 0;
                    if (!hasRoad) continue;
                    var child = new GameObject($"Road details {key.x}, {key.y}");
                    child.transform.SetParent(holder.transform, false);
                    child.transform.localPosition = new Vector3(center.x, 0f, center.y);
                    bool painted = false;
                    if (hasRoad)
                    {
                        var half = indexed.Size * .5f;
                        // The preview offsets the surveyed vertices before converting them to floats.
                        var points = new Vector3[footprint.Count];
                        for (int i = 0; i < points.Length; i++)
                        {
                            if (!(footprint[i] is JArray xy) || xy.Count != 2)
                                throw new FormatException("Invalid road vertex.");
                            points[i] = new Vector3((float)((double)xy[0] - center.x), RoadMesh.SurfaceY,
                                (float)((double)xy[1] - center.y));
                        }
                        var triangles = indices.ToObject<List<int>>();
                        foreach (int index in triangles)
                            if (index < 0 || index >= points.Length) throw new FormatException("Invalid road triangle.");
                        var builder = new LtaCityRoadPaint.PaintMeshBuilder(points, triangles, Vector2.zero, half, useCoverage)
                            { DrawArrowsAsSymbols = symbols != null };
                        painted = child.AddComponent<LtaCityRoadPaint>().Build(tile, white, yellow, half, builder, meshes.Add);
                        if (painted) built++;
                    }
                    if (!painted) WorldBuilder.Remove(child);
                    if (clock.Elapsed.TotalMilliseconds >= 4)
                    {
                        await Task.Yield();
                        clock.Restart();
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                if (disposed) throw new OperationCanceledException();
                if (built == 0) return false;
                if (symbols != null)
                {
                    LtaOverviewSymbols.Build(indexed.Tiles, holder.transform, symbols, meshes.Add);
                }
                if (originalAsphalt.HasProperty("_MappedPaintReady"))
                    originalAsphalt.SetFloat("_MappedPaintReady", 1f);
                owned.AddRange(meshes);
                committed = true;
                Debug.Log($"[CityView] LTA paint over original asphalt on {built} tiles.");
                return true;
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                Debug.LogWarning($"[CityView] Using the original road markings: {error.Message}");
                return false;
            }
            finally
            {
                if (!committed)
                {
                    if (holder != null)
                    {
                        holder.SetActive(false);
                        WorldBuilder.Remove(holder);
                    }
                    if (roadRenderer != null)
                    {
                        var fallback = roadRenderer.sharedMaterials[RoadMesh.AsphaltLayer];
                        if (fallback.HasProperty("_MappedPaintReady")) fallback.SetFloat("_MappedPaintReady", 0f);
                    }
                    foreach (var mesh in meshes)
                    {
                        if (Application.isPlaying) UnityEngine.Object.Destroy(mesh);
                        else UnityEngine.Object.DestroyImmediate(mesh);
                    }
                }
            }
        }
    }
}
