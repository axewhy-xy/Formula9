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
            if (asset == null)
            {
                Debug.LogWarning($"[CityView] LTA paint overlay missing Resources/Formula9/RoadData/{area}-city-map.json.");
                return false;
            }

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
                var roadMesh = roadRenderer.GetComponent<MeshFilter>().sharedMesh;
                var roadVertices = roadMesh.vertices;
                var asphalt = roadMesh.GetTriangles(RoadMesh.AsphaltLayer);
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
                    var child = new GameObject($"Road details {key.x}, {key.y}");
                    child.transform.SetParent(holder.transform, false);
                    child.transform.localPosition = new Vector3(center.x, 0f, center.y);
                    float half = indexed.Size * .5f;
                    var points = new List<Vector3>();
                    var triangles = new List<int>();
                    for (int i = 0; i < asphalt.Length; i += 3)
                    {
                        var a = roadVertices[asphalt[i]];
                        var b = roadVertices[asphalt[i + 1]];
                        var c = roadVertices[asphalt[i + 2]];
                        if (Mathf.Min(a.x, Mathf.Min(b.x, c.x)) > center.x + half ||
                            Mathf.Max(a.x, Mathf.Max(b.x, c.x)) < center.x - half ||
                            Mathf.Min(a.z, Mathf.Min(b.z, c.z)) > center.y + half ||
                            Mathf.Max(a.z, Mathf.Max(b.z, c.z)) < center.y - half)
                            continue;
                        var offset = new Vector3(center.x, 0, center.y);
                        triangles.Add(points.Count);
                        triangles.Add(points.Count + 1);
                        triangles.Add(points.Count + 2);
                        points.Add(a - offset);
                        points.Add(b - offset);
                        points.Add(c - offset);
                    }
                    bool painted = false;
                    // The mesh is placement context. Survey paint can cross gaps in it.
                    if (tile["markings"] != null)
                    {
                        var builder = new LtaCityRoadPaint.PaintMeshBuilder(
                            points.ToArray(), triangles, Vector2.zero, half, useCoverage)
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
                if (built == 0)
                {
                    Debug.LogWarning($"[CityView] LTA paint overlay generated zero meshes from {indexed.Tiles.Count} tiles.");
                    return false;
                }
                if (symbols != null)
                {
                    LtaOverviewSymbols.Build(indexed.Tiles, holder.transform, symbols, meshes.Add);
                }
                owned.AddRange(meshes);
                committed = true;
                Debug.Log($"[CityView] LTA paint overlay loaded on {built} tiles.");
                return true;
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                Debug.LogWarning($"[CityView] LTA paint overlay failed: {error}");
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
