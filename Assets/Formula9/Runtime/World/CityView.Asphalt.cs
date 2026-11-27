using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using System.Linq;

namespace Formula9.World
{
    internal sealed class AsphaltTriangleSpatialIndex
    {
        private readonly struct BoundsXZ
        {
            public readonly float MinX, MinZ, MaxX, MaxZ;

            public BoundsXZ(Vector3 a, Vector3 b, Vector3 c)
            {
                MinX = Math.Min(a.x, Math.Min(b.x, c.x));
                MinZ = Math.Min(a.z, Math.Min(b.z, c.z));
                MaxX = Math.Max(a.x, Math.Max(b.x, c.x));
                MaxZ = Math.Max(a.z, Math.Max(b.z, c.z));
            }

            public bool Overlaps(float minX, float minZ, float maxX, float maxZ) =>
                MinX <= maxX && MaxX >= minX && MinZ <= maxZ && MaxZ >= minZ;
        }

        private readonly float cellSize;
        private readonly BoundsXZ[] bounds;
        private readonly Dictionary<(int X, int Z), List<int>> cells =
            new Dictionary<(int X, int Z), List<int>>();

        public AsphaltTriangleSpatialIndex(Vector3[] vertices, int[] triangles, float cellSize,
            CancellationToken cancellation)
        {
            if (!(cellSize > 0) || float.IsInfinity(cellSize) || float.IsNaN(cellSize))
                throw new ArgumentOutOfRangeException(nameof(cellSize));

            this.cellSize = cellSize;
            bounds = new BoundsXZ[triangles.Length / 3];

            for (int i = 0; i < triangles.Length; i += 3)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();

                int triangle = i / 3;
                var triangleBounds = new BoundsXZ(
                    vertices[triangles[i]], vertices[triangles[i + 1]], vertices[triangles[i + 2]]);
                bounds[triangle] = triangleBounds;

                for (int x = Cell(triangleBounds.MinX); x <= Cell(triangleBounds.MaxX); x++)
                for (int z = Cell(triangleBounds.MinZ); z <= Cell(triangleBounds.MaxZ); z++)
                {
                    var key = (x, z);
                    if (!cells.TryGetValue(key, out var entries))
                        cells[key] = entries = new List<int>();
                    entries.Add(triangle);
                }
            }
        }

        public int[] Query(Vector2 center, float half)
        {
            float minX = center.x - half, minZ = center.y - half;
            float maxX = center.x + half, maxZ = center.y + half;
            var found = new HashSet<int>();

            for (int x = Cell(minX); x <= Cell(maxX); x++)
            for (int z = Cell(minZ); z <= Cell(maxZ); z++)
                if (cells.TryGetValue((x, z), out var entries))
                    foreach (int triangle in entries)
                        if (bounds[triangle].Overlaps(minX, minZ, maxX, maxZ))
                            found.Add(triangle);

            return found.OrderBy(triangle => triangle).ToArray();
        }

        private int Cell(float coordinate) => (int)Math.Floor((double)coordinate / cellSize);
    }
    public sealed partial class CityView
    {
        // The legacy road mesh is the only pavement. The LTA export supplies paint records.
        private async Task<bool> LoadRoadMarkingsAsync(string area, MeshRenderer roadRenderer, Shader shader, Material asphaltMaterial, CancellationToken cancellation)
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
                    return (Tiles: tiles, Size: size, Bounds: map["bounds_m"]);
                }, cancellation);
               cancellation.ThrowIfCancellationRequested();
                if (disposed) throw new OperationCanceledException();

                LoadCentreDividerMask(area, asphaltMaterial, indexed.Bounds);

                
                var coverageShader = Resources.Load<Shader>("Formula9/Shaders/LtaRoadPaintCoverage");
                bool useCoverage = coverageShader != null && coverageShader.isSupported;

                if (!useCoverage)
                    Debug.LogWarning("[CityView] LtaRoadPaintCoverage shader unavailable; using fixed-width LTA paint.");

                Material Paint(string name, Color colour)
                {
                   
                    var mat = Own(new Material(useCoverage ? coverageShader : shader)
                        { name = name, color = colour, renderQueue = 2460 });
                    if (!useCoverage && mat.HasProperty("_StencilComp"))
                    {
                        mat.SetInt("_StencilComp", (int)UnityEngine.Rendering.CompareFunction.Always);
                    }
                    if (mat.HasProperty("_ZWrite")) mat.SetInt("_ZWrite", 0);
                    return mat;
                }

                var symbolShader = Resources.Load<Shader>("Formula9/Shaders/LtaReadableMarkings");
                var white = Paint("WhitePaint", MarkingColor);
                var yellow = Paint("YellowPaint", new Color(.94f, .73f, .13f));
                holder = new GameObject("LTA paint over original roads");
                holder.transform.SetParent(root, false);
                var roadMesh = roadRenderer.GetComponent<MeshFilter>().sharedMesh;
                var asphaltTriangles = roadMesh.GetTriangles(RoadMesh.AsphaltLayer);
                var roadVertices = roadMesh.vertices;
                var asphaltIndex = await Task.Run(
                    () => new AsphaltTriangleSpatialIndex(roadVertices, asphaltTriangles, indexed.Size, cancellation),
                    cancellation);
                cancellation.ThrowIfCancellationRequested();


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
                    foreach (int triangle in asphaltIndex.Query(center, half))
                    {
                        int i = triangle * 3;
                        var a = roadVertices[asphaltTriangles[i]];
                        var b = roadVertices[asphaltTriangles[i + 1]];
                        var c = roadVertices[asphaltTriangles[i + 2]];
                        var offset = new Vector3(center.x, 0, center.y);

                        triangles.Add(points.Count);
                        triangles.Add(points.Count + 1);
                        triangles.Add(points.Count + 2);
                        points.Add(a - offset);
                        points.Add(b - offset);
                        points.Add(c - offset);

                        if (clock.Elapsed.TotalMilliseconds >= 4)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (disposed) throw new OperationCanceledException();
                            await Task.Yield();
                            clock.Restart();
                        }
                    }
                    bool painted = false;
                    // The mesh is placement context. Survey paint can cross gaps in it.
                    if (tile["markings"] != null)
                    {
                        var builder = new LtaCityRoadPaint.PaintMeshBuilder(points.ToArray(), triangles, Vector2.zero, half, useCoverage);
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
               
                owned.AddRange(meshes);
                committed = true;
                if (asphaltMaterial.HasProperty("_MappedPaintReady"))
                    asphaltMaterial.SetFloat("_MappedPaintReady", 1f);
                Debug.Log($"[CityView] LTA paint overlay loaded on {built} tiles.");
                return true;
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                Debug.LogWarning($"[CityView] LTA paint overlay failed: {error}");
                return false;
            }
            catch (OperationCanceledException)
            {
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
                   
                    foreach (var mesh in meshes)
                    {
                        if (Application.isPlaying) UnityEngine.Object.Destroy(mesh);
                        else UnityEngine.Object.DestroyImmediate(mesh);
                    }
                }
            }
        }

       internal static bool TryCreateSurveyMaskBounds(JToken source, out Vector4 bounds)
        {
            bounds = default;
            if (!(source is JArray)) return false;

            var values = (JArray)source;
            if (values.Count != 4) return false;

            for (int i = 0; i < values.Count; i++)
            {
                if (values[i].Type != JTokenType.Float &&
                    values[i].Type != JTokenType.Integer)
                    return false;
            }

            float minX = (float)values[0];
            float minZ = (float)values[1];
            float maxX = (float)values[2];
            float maxZ = (float)values[3];
            float width = maxX - minX;
            float height = maxZ - minZ;
            if (float.IsNaN(minX) || float.IsInfinity(minX) ||
                float.IsNaN(minZ) || float.IsInfinity(minZ) ||
                float.IsNaN(width) || float.IsInfinity(width) || width <= 0 ||
                float.IsNaN(height) || float.IsInfinity(height) || height <= 0)
                return false;

            float inverseWidth = 1f / width;
            float inverseHeight = 1f / height;
            if (float.IsInfinity(inverseWidth) || float.IsInfinity(inverseHeight))
                return false;

            bounds = new Vector4(minX, minZ, inverseWidth, inverseHeight);
            return true;
        }
    }
}
