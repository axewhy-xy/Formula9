using System;
using System.Collections.Generic;
using System.Linq;
using Formula9.Backend;
using UnityEngine;
using UnityEngine.Rendering;

namespace Formula9.World
{
    // Draws and moves the background traffic sample from /api/traffic/{area}.
    public sealed partial class CityTrafficRenderer : IDisposable
    {
        private const float LaneWidthM = 3.2f;
        private const int BatchSize = 1023;
        // A long frame moves traffic this far at most, so a hitch cannot throw vehicles along the road.
        private const float MaxStepSeconds = 0.1f;
        // How fast a drawn vehicle closes on its place in the lane, as it changes lane at a junction
        private const float CatchUpFactor = 1.5f, MinCatchUpMps = 6f;
        private const float MinTurnRadiusM = 4f, MinTurnRateRad = 0.8f;
        // Keeps a vehicle's shadow and length in view as it leaves the camera's frustum
        private const float CullMarginM = 25f;

        private static readonly int PaintId = Shader.PropertyToID("_Paint");
        private static readonly int VertexColorId = Shader.PropertyToID("_VertexColor");
        private static readonly string[] CopiedColors = { "_Color" };
        private static readonly string[] CopiedFloats = { "_Glossiness", "_Metallic" };

        private readonly Material template;
        private readonly Material blockMaterial;
        private readonly Dictionary<string, DirectedRoad> roads = new Dictionary<string, DirectedRoad>();
        private readonly Dictionary<long, List<DirectedRoad>> exitsByNode = new Dictionary<long, List<DirectedRoad>>();
        private readonly List<DirectedRoad> entryRoads = new List<DirectedRoad>();
        private readonly List<DirectedRoad> largeEntryRoads = new List<DirectedRoad>();
        private readonly Dictionary<string, List<ModelRenderData>> modelRenderers = new Dictionary<string, List<ModelRenderData>>();
        private readonly Dictionary<Material, Material> materialCopies = new Dictionary<Material, Material>();
        private readonly List<VehicleState> vehicles = new List<VehicleState>();
        private readonly Plane[] frustum = new Plane[6];

        public int VehicleCount => vehicles.Count;

        public CityTrafficRenderer()
        {
            // An asset, not a material made here: a player build keeps the shader's instancing variants
            // only for a material saved with instancing on.
            template = Resources.Load<Material>("Formula9/TrafficVehicle");
            if (template == null)
            {
                Debug.LogError("The traffic vehicle material is missing, so no traffic is drawn.");
                return;
            }
            blockMaterial = new Material(template);
            blockMaterial.SetFloat(VertexColorId, 1);
        }

        public void Rebuild(IReadOnlyList<CityRoad> roadsData, CityTrafficData trafficData, CitySignalData signals = null)
        {
            roads.Clear();
            roadGrid.Clear();
            filledRoads.Clear();
            hazardRoads.Clear();
            claimsByNode.Clear();
            redLightRuns = 0;
            exitsByNode.Clear();
            entryRoads.Clear();
            largeEntryRoads.Clear();
            ClearModels();
            vehicles.Clear();

            BuildDirectedRoads(roadsData);
            PlaceSignals(signals);
            if (template == null || roads.Count == 0 || trafficData == null) return;
            FindEntryRoads();

            var catalog = Resources.Load<TrafficVehicleCatalog>("Formula9/TrafficVehicleCatalog");
            if (catalog == null)
                Debug.LogWarning("Traffic vehicle catalog is missing, so traffic is drawn as plain blocks.");
            foreach (var pair in trafficData.Models)
                modelRenderers[pair.Key] = ModelsFor(catalog, pair.Key, pair.Value);

            foreach (var seed in trafficData.Vehicles)
                AddVehicle(seed, trafficData);
        }

        // A delta of zero holds the traffic still, and still draws it. A camera culls what it cannot see.
        public void Tick(float deltaTime, Camera camera = null)
        {
            if (vehicles.Count == 0) return;

            var step = Mathf.Min(deltaTime, MaxStepSeconds);
            if (step > 0) Drive(step);
            for (var i = 0; i < vehicles.Count; i++)
                UpdatePose(vehicles[i], step);

            RenderVehicles(camera);
        }

        public bool TryGetPose(string vehicleId, out Vector3 position, out Vector3 forward)
        {
            foreach (var vehicle in vehicles)
            {
                if (vehicle.Id != vehicleId || !vehicle.Placed) continue;
                position = vehicle.Position;
                forward = vehicle.Forward;
                return true;
            }
            position = forward = default;
            return false;
        }

        // Where a prefab's meshes end up relative to a vehicle's place on the road, for a given length.
        public static Bounds ModelBounds(GameObject prefab, float yawDegrees, float lengthM)
        {
            var renderers = ModelRenderData.DrawnRenderers(prefab);
            var placement = ModelRenderData.Placement(prefab, renderers, yawDegrees, lengthM);
            return ModelRenderData.Measure(renderers, placement);
        }

        public void Dispose()
        {
            ClearModels();
            DestroyObject(blockMaterial);
        }

        private void ClearModels()
        {
            foreach (var list in modelRenderers.Values)
                foreach (var render in list) render.Dispose();
            modelRenderers.Clear();
            foreach (var copy in materialCopies.Values) DestroyObject(copy);
            materialCopies.Clear();
        }

        private static void DestroyObject(UnityEngine.Object item)
        {
            if (item == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(item);
            else UnityEngine.Object.DestroyImmediate(item);
        }

        // Imported prefabs keep their own look. A model with no prefab stays a built shape.
        private List<ModelRenderData> ModelsFor(TrafficVehicleCatalog catalog, string modelId, CityTrafficModel model)
        {
            var made = new List<ModelRenderData>();
            if (catalog != null && catalog.entries != null)
            {
                foreach (var entry in catalog.entries)
                {
                    if (entry == null || entry.modelId != modelId || entry.prefab == null) continue;
                    var built = ModelRenderData.FromPrefab(entry, Mathf.Max(1f, model.LengthM), CopyOf);
                    if (built != null) made.Add(built);
                }
            }
            if (made.Count == 0)
            {
                // The project has no motorcycle asset, so its built shape is the intended one.
                if (catalog != null && modelId != "motorcycle")
                    Debug.LogWarning("No imported vehicle for '" + modelId + "', so that traffic is still a plain block.");
                made.Add(ModelRenderData.FromMesh(BuildMesh(modelId, model), blockMaterial));
            }
            return made;
        }

        // One instancing copy of each imported material, whichever vehicles and parts share it.
        private Material CopyOf(Material source)
        {
            if (source == null) return template;
            if (materialCopies.TryGetValue(source, out var copy)) return copy;
            copy = new Material(template) { name = source.name };
            if (source.HasProperty("_MainTex"))
            {
                copy.mainTexture = source.mainTexture;
                copy.mainTextureScale = source.mainTextureScale;
                copy.mainTextureOffset = source.mainTextureOffset;
            }
            foreach (var name in CopiedColors)
                if (source.HasProperty(name)) copy.SetColor(name, source.GetColor(name));
            foreach (var name in CopiedFloats)
                if (source.HasProperty(name)) copy.SetFloat(name, source.GetFloat(name));
            materialCopies[source] = copy;
            return copy;
        }

        private void BuildDirectedRoads(IReadOnlyList<CityRoad> source)
        {
            if (source == null) return;
            foreach (var road in source)
            {
                if (road?.Points == null || road.Points.Count < 2) continue;
                AddDirectedRoad(road, 1);
                if (!road.Oneway) AddDirectedRoad(road, -1);
            }
        }

        private void AddDirectedRoad(CityRoad road, int direction)
        {
            var points = direction == 1 ? road.Points : road.Points.AsEnumerable().Reverse().ToList();
            var path = points.Select(p => new Vector3((float)p.X, RoadMesh.SurfaceY + 0.02f, (float)p.Y)).ToArray();
            var directed = DirectedRoad.Create(road, direction, path);
            if (directed.Length < 0.1f) return;

            roads[directed.Key] = directed;
            if (!exitsByNode.TryGetValue(directed.FromNode, out var exits))
                exitsByNode[directed.FromNode] = exits = new List<DirectedRoad>();
            exits.Add(directed);
        }

        // Where traffic comes in from outside the area: roads that no other road leads into.
        private void FindEntryRoads()
        {
            var arriving = roads.Values.ToLookup(road => road.ToNode);
            foreach (var road in roads.Values)
            {
                if (arriving[road.FromNode].Any(other => other.RoadId != road.RoadId)) continue;
                entryRoads.Add(road);
                if (road.Class != "service") largeEntryRoads.Add(road);
            }
        }

        private void AddVehicle(CityTrafficVehicle seed, CityTrafficData trafficData)
        {
            if (seed == null || string.IsNullOrEmpty(seed.Model)) return;
            if (!modelRenderers.TryGetValue(seed.Model, out var options) || options.Count == 0) return;
            var render = options[(int)(Hash(seed.Id ?? seed.Model) % (uint)options.Count)];

            if (!roads.TryGetValue($"{seed.Road}|{seed.Direction}", out var road))
                road = roads.Values.FirstOrDefault(r => r.RoadId == seed.Road);
            if (road == null) return;

            var lane = Mathf.Clamp(seed.Lane, 0, Mathf.Max(0, road.Lanes - 1));
            var paint = ParseColor(seed.Color);
            var state = new VehicleState
            {
                Id = seed.Id ?? $"{seed.Model}-{vehicles.Count + 1}",
                Model = seed.Model,
                Road = road,
                Lane = lane,
                Distance = Mathf.Clamp(seed.DistanceM, 0, road.Length),
                Speed = Mathf.Max(0, seed.SpeedKph / 3.6f * 0.5f),
                SpeedFactor = 0.85f + 0.2f * Random01(Hash(seed.Id ?? seed.Model)),
                Rng = Hash(seed.Id ?? seed.Model),
                Paint = QualitySettings.activeColorSpace == ColorSpace.Linear ? paint.linear : paint,
                TurnRadius = Mathf.Max(MinTurnRadiusM, trafficData.Models[seed.Model].LengthM),
                Length = trafficData.Models[seed.Model].LengthM,
                HalfWidth = trafficData.Models[seed.Model].WidthM / 2,
                Render = render
            };
            vehicles.Add(state);
        }

        // The vehicle's place on the road jumps at a bend or a junction. What is drawn closes on it, so
        // the vehicle steers across instead of snapping.
        private static void UpdatePose(VehicleState vehicle, float dt)
        {
            if (vehicle.Outside) return;
            if (!vehicle.Road.Sample(vehicle.Distance, vehicle.Road.LaneOffset(vehicle.Lane) + vehicle.Aside, out var target, out var tangent)) return;
            if (!vehicle.Placed)
            {
                vehicle.Position = target;
                vehicle.Forward = tangent;
                vehicle.Placed = true;
                return;
            }
            if (dt <= 0) return;
            vehicle.Position = Vector3.MoveTowards(vehicle.Position, target, Mathf.Max(vehicle.Speed * CatchUpFactor, MinCatchUpMps) * dt);
            var turnRate = Mathf.Max(vehicle.Speed / vehicle.TurnRadius, MinTurnRateRad);
            vehicle.Forward = Vector3.RotateTowards(vehicle.Forward, tangent, turnRate * dt, 0);
        }

        private static float Acceleration(string model) =>
            model == "motorcycle" ? 4f : model == "heavy_truck" ? 1.5f : model == "truck" || model == "bus" ? 2f : 3f;

        private static bool IsLarge(string model) => model == "bus" || model == "truck" || model == "heavy_truck";

        private DirectedRoad ChooseNextRoad(VehicleState vehicle, DirectedRoad from)
        {
            if (!exitsByNode.TryGetValue(from.ToNode, out var exits)) return null;

            // Large vehicles keep off service roads, the rule the backend places them by. They turn
            // back, or leave the area, instead of taking one.
            var allowed = exits.Where(road => !(IsLarge(vehicle.Model) && road.Class == "service")).ToList();
            // A road with a hazard on it is taken only if there is no other way, even back
            var open = allowed.Where(road => road.Hazards.Count == 0).ToList();
            if (open.Count > 0) allowed = open;
            var candidates = allowed.Where(road => road.RoadId != from.RoadId).ToList();
            if (candidates.Count == 0) candidates = allowed;
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];

            var weighted = new List<(DirectedRoad Road, float Weight)>(candidates.Count);
            foreach (var candidate in candidates)
            {
                var straightness = Mathf.InverseLerp(180f, 0f, Vector3.Angle(from.EndDirection, candidate.StartDirection));
                var weight = ClassWeight(candidate.Class) * Mathf.Lerp(0.4f, 1.6f, straightness);
                weighted.Add((candidate, Mathf.Max(0.01f, weight)));
            }

            var total = weighted.Sum(w => w.Weight);
            var pick = Random01(vehicle.Rng = Step(vehicle.Rng)) * total;
            foreach (var item in weighted)
            {
                pick -= item.Weight;
                if (pick <= 0) return item.Road;
            }
            return weighted[weighted.Count - 1].Road;
        }

        private void RenderVehicles(Camera camera)
        {
            foreach (var list in modelRenderers.Values)
                foreach (var render in list) render.Reset();
            if (camera != null) GeometryUtility.CalculateFrustumPlanes(camera, frustum);
            foreach (var vehicle in vehicles)
            {
                if (!vehicle.Placed || (camera != null && !InView(vehicle.Position))) continue;
                vehicle.Render.Add(Matrix4x4.TRS(vehicle.Position, Quaternion.LookRotation(vehicle.Forward, Vector3.up), Vector3.one), vehicle.Paint);
            }

            foreach (var list in modelRenderers.Values)
                foreach (var render in list) render.Render();
        }

        private bool InView(Vector3 position)
        {
            foreach (var plane in frustum)
                if (plane.GetDistanceToPoint(position) < -CullMarginM) return false;
            return true;
        }

        private static float ClassWeight(string roadClass) =>
            roadClass == "primary" ? 8f :
            roadClass == "primary_link" ? 4f :
            roadClass == "secondary" ? 5f :
            roadClass == "secondary_link" ? 3f :
            roadClass == "tertiary" ? 3f :
            roadClass == "tertiary_link" ? 2f :
            roadClass == "living_street" ? 0.5f :
            roadClass == "service" ? 0.5f : 1f;

        private static Color ParseColor(string hex)
        {
            if (!string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out var parsed)) return parsed;
            return Color.white;
        }

        private static uint Hash(string text)
        {
            unchecked
            {
                var hash = 2166136261u;
                foreach (var c in text ?? "")
                {
                    hash ^= c;
                    hash *= 16777619u;
                }
                return hash == 0 ? 1u : hash;
            }
        }

        private static uint Step(uint state) => 1664525u * state + 1013904223u;

        private static float Random01(uint state) => (Step(state) & 0x00FFFFFF) / 16777215f;

        private static Mesh BuildMesh(string modelId, CityTrafficModel model)
        {
            var length = Mathf.Max(1f, model.LengthM);
            var width = Mathf.Max(0.4f, model.WidthM);
            var height = Mathf.Max(0.6f, model.HeightM);
            var mesh = new Mesh { name = $"Traffic {modelId}" };
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var colors = new List<Color>();
            var dark = new Color(0.12f, 0.12f, 0.14f, 1);

            if (modelId == "motorcycle")
            {
                // Two wheels in line, the frame between them and a rider, as narrow as the model says.
                var wheelSize = new Vector3(width * 0.2f, height * 0.4f, length * 0.28f);
                AddBox(vertices, triangles, colors, new Vector3(0, height * 0.2f, length * 0.34f), wheelSize, dark);
                AddBox(vertices, triangles, colors, new Vector3(0, height * 0.2f, -length * 0.34f), wheelSize, dark);
                AddBox(vertices, triangles, colors, new Vector3(0, height * 0.4f, 0), new Vector3(width * 0.4f, height * 0.3f, length * 0.62f), Color.white);
                AddBox(vertices, triangles, colors, new Vector3(0, height * 0.72f, -length * 0.06f), new Vector3(width * 0.6f, height * 0.36f, length * 0.2f), dark);
                AddBox(vertices, triangles, colors, new Vector3(0, height * 0.94f, -length * 0.04f), new Vector3(width * 0.34f, height * 0.12f, length * 0.12f), Color.white);
            }
            else
            {
                AddBox(vertices, triangles, colors, Vector3.zero + Vector3.up * (height * 0.28f), new Vector3(width, height * 0.56f, length), Color.white);
                var cabinLength = modelId == "heavy_truck" ? length * 0.35f : length * 0.52f;
                var cabinCenter = new Vector3(0, height * 0.74f, length * 0.1f);
                var cabinSize = new Vector3(width * 0.88f, height * 0.42f, cabinLength);
                AddBox(vertices, triangles, colors, cabinCenter, cabinSize, dark);
            }

            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.SetColors(colors);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void AddBox(List<Vector3> vertices, List<int> triangles, List<Color> colors, Vector3 center, Vector3 size, Color color)
        {
            var hx = size.x * 0.5f;
            var hy = size.y * 0.5f;
            var hz = size.z * 0.5f;
            var p = new[]
            {
                center + new Vector3(-hx, -hy, -hz), center + new Vector3(hx, -hy, -hz), center + new Vector3(hx, hy, -hz), center + new Vector3(-hx, hy, -hz),
                center + new Vector3(-hx, -hy, hz),  center + new Vector3(hx, -hy, hz),  center + new Vector3(hx, hy, hz),  center + new Vector3(-hx, hy, hz),
            };
            AddFace(vertices, triangles, colors, p[0], p[1], p[2], p[3], color);
            AddFace(vertices, triangles, colors, p[5], p[4], p[7], p[6], color);
            AddFace(vertices, triangles, colors, p[4], p[0], p[3], p[7], color);
            AddFace(vertices, triangles, colors, p[1], p[5], p[6], p[2], color);
            AddFace(vertices, triangles, colors, p[3], p[2], p[6], p[7], color);
            AddFace(vertices, triangles, colors, p[4], p[5], p[1], p[0], color);
        }

        private static void AddFace(List<Vector3> vertices, List<int> triangles, List<Color> colors, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
        {
            var start = vertices.Count;
            vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
            colors.Add(color); colors.Add(color); colors.Add(color); colors.Add(color);
            triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
            triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 3);
        }

        private sealed class ModelRenderData : IDisposable
        {
            // Filled and drawn within one call, so every model can share them.
            private static readonly Matrix4x4[] BatchMatrices = new Matrix4x4[BatchSize];
            private static readonly Vector4[] BatchPaints = new Vector4[BatchSize];

            private readonly List<Part> parts = new List<Part>();
            private readonly List<Matrix4x4> matrices = new List<Matrix4x4>();
            private readonly List<Vector4> paints = new List<Vector4>();
            private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
            private readonly Mesh ownedMesh;

            private ModelRenderData(Mesh ownedMesh) => this.ownedMesh = ownedMesh;

            public static ModelRenderData FromMesh(Mesh mesh, Material material)
            {
                var data = new ModelRenderData(mesh);
                data.parts.Add(new Part { mesh = mesh, material = material, local = Matrix4x4.identity, paint = true, shadows = true });
                return data;
            }

            // Reads the prefab as it stands, with no copy of it in the scene, so nothing on it runs.
            // Every vehicle of this model then shares its meshes, scaled to the traffic sample's length.
            public static ModelRenderData FromPrefab(TrafficVehicleCatalog.Entry entry, float lengthM, Func<Material, Material> copyOf)
            {
                var renderers = DrawnRenderers(entry.prefab);
                if (renderers.Count == 0) return null;
                var placement = Placement(entry.prefab, renderers, entry.yawDegrees, lengthM);

                // The biggest part is the bodywork. Wheels take no paint and cast no shadow worth its cost.
                var body = renderers.OrderByDescending(r => { var s = MeshOf(r).bounds.size; return s.x * s.y * s.z; }).First();
                var data = new ModelRenderData(null);
                foreach (var renderer in renderers)
                {
                    var mesh = MeshOf(renderer);
                    var materials = renderer.sharedMaterials;
                    var count = Mathf.Max(1, mesh.subMeshCount);
                    for (var submesh = 0; submesh < count; submesh++)
                    {
                        var source = entry.material != null ? entry.material
                            : submesh < materials.Length && materials[submesh] != null ? materials[submesh]
                            : materials.FirstOrDefault(m => m != null);
                        data.parts.Add(new Part
                        {
                            mesh = mesh,
                            submesh = Mathf.Min(submesh, mesh.subMeshCount - 1),
                            material = copyOf(source),
                            local = placement * renderer.transform.localToWorldMatrix,
                            paint = entry.paintable && renderer == body,
                            shadows = renderer == body,
                        });
                    }
                }
                return data;
            }

            public static List<Renderer> DrawnRenderers(GameObject prefab) =>
                prefab.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled && MeshOf(r) != null).ToList();

            // From world space to a vehicle's own: facing +Z, standing on y = 0, centred, and the right length.
            public static Matrix4x4 Placement(GameObject prefab, List<Renderer> renderers, float yawDegrees, float lengthM)
            {
                // Where the prefab happens to stand does not matter, but its root's scale is part of the model
                var toRoot = Matrix4x4.TRS(prefab.transform.position, prefab.transform.rotation, Vector3.one).inverse;
                var turned = Matrix4x4.Rotate(Quaternion.Euler(0f, yawDegrees, 0f)) * toRoot;
                var bounds = Measure(renderers, turned);
                if (bounds.size.x > bounds.size.z)
                {
                    turned = Matrix4x4.Rotate(Quaternion.Euler(0f, 90f, 0f)) * turned;
                    bounds = Measure(renderers, turned);
                }
                var scale = Mathf.Max(0.2f, lengthM) / Mathf.Max(bounds.size.z, 0.01f);
                var pivot = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
                return Matrix4x4.Scale(Vector3.one * scale) * Matrix4x4.Translate(-pivot) * turned;
            }

            public static Bounds Measure(List<Renderer> renderers, Matrix4x4 placement)
            {
                Bounds bounds = default;
                var any = false;
                foreach (var renderer in renderers)
                {
                    var toPlaced = placement * renderer.transform.localToWorldMatrix;
                    var box = MeshOf(renderer).bounds;
                    for (var corner = 0; corner < 8; corner++)
                    {
                        var at = box.center + Vector3.Scale(box.extents, new Vector3((corner & 1) * 2 - 1, (corner >> 1 & 1) * 2 - 1, (corner >> 2 & 1) * 2 - 1));
                        var point = toPlaced.MultiplyPoint3x4(at);
                        if (!any) { bounds = new Bounds(point, Vector3.zero); any = true; }
                        else bounds.Encapsulate(point);
                    }
                }
                return bounds;
            }

            public void Add(Matrix4x4 matrix, Color paint)
            {
                matrices.Add(matrix);
                paints.Add(paint);
            }

            public void Reset()
            {
                matrices.Clear();
                paints.Clear();
            }

            public void Render()
            {
                if (matrices.Count == 0) return;
                foreach (var part in parts)
                {
                    var shadows = part.shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                    if (!SystemInfo.supportsInstancing)
                    {
                        RenderOneByOne(part, shadows);
                        continue;
                    }
                    for (var start = 0; start < matrices.Count; start += BatchSize)
                    {
                        var count = Mathf.Min(BatchSize, matrices.Count - start);
                        for (var i = 0; i < count; i++)
                        {
                            BatchMatrices[i] = matrices[start + i] * part.local;
                            BatchPaints[i] = paints[start + i];
                        }
                        if (part.paint) block.SetVectorArray(PaintId, BatchPaints);
                        Graphics.DrawMeshInstanced(part.mesh, part.submesh, part.material, BatchMatrices, count,
                            part.paint ? block : null, shadows, receiveShadows: true, layer: 0, camera: null, LightProbeUsage.Off);
                    }
                }
            }

            private void RenderOneByOne(Part part, ShadowCastingMode shadows)
            {
                for (var i = 0; i < matrices.Count; i++)
                {
                    if (part.paint) block.SetVector(PaintId, paints[i]);
                    Graphics.DrawMesh(part.mesh, matrices[i] * part.local, part.material, 0, null, part.submesh, part.paint ? block : null, shadows, true);
                }
            }

            public void Dispose() => DestroyObject(ownedMesh);

            private static Mesh MeshOf(Renderer renderer)
            {
                if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
                var filter = renderer.GetComponent<MeshFilter>();
                return filter != null ? filter.sharedMesh : null;
            }

            private struct Part
            {
                public Mesh mesh;
                public int submesh;
                public Material material;
                public Matrix4x4 local;
                public bool paint, shadows;
            }
        }

        private sealed class VehicleState
        {
            public string Id, Model;
            public DirectedRoad Road;
            // The roads it means to take after this one, and the junctions it has been given way at
            public readonly List<DirectedRoad> Ahead = new List<DirectedRoad>();
            public readonly List<Claim> Claims = new List<Claim>();
            public int Lane, LaneIndex;
            public float Distance, Speed, TargetSpeed, SpeedFactor, TurnRadius, Length, HalfWidth;
            // How long it has stood still, and been kept waiting by a junction
            public float StoppedS, WaitS;
            // Metres to the left of its lane, to let a shuttle by
            public float Aside, TargetAside;
            // Off the map, waiting for room on a road into the area
            public bool Outside;
            public uint Rng;
            public Color Paint;
            public ModelRenderData Render;
            // What is drawn: it follows the vehicle's place on the road, and is unset until first placed.
            public bool Placed;
            public Vector3 Position, Forward;

            public bool HasClaim(DirectedRoad to)
            {
                foreach (var claim in Claims)
                    if (claim.To == to && !claim.Entered) return true;
                return false;
            }

            public bool HasClaimFrom(DirectedRoad from)
            {
                foreach (var claim in Claims)
                    if (claim.From == from && !claim.Entered) return true;
                return false;
            }
        }

        private sealed class DirectedRoad
        {
            public string Key, RoadId, Class;
            public int Lanes;
            public float SpeedMps;
            public bool Oneway;
            public long FromNode, ToNode;
            public Vector3[] Points;
            public float[] Along;
            public float Length;
            public Vector3 StartDirection, EndDirection;
            // Each lane's vehicles, back to front, as of this step
            public List<VehicleState>[] Traffic;
            public bool Filled;
            public readonly List<StopLine> Stops = new List<StopLine>();
            public readonly List<Span> Hazards = new List<Span>();

            public static DirectedRoad Create(CityRoad source, int direction, Vector3[] points)
            {
                var along = new float[points.Length];
                var length = 0f;
                for (var i = 1; i < points.Length; i++)
                {
                    length += Vector3.Distance(points[i - 1], points[i]);
                    along[i] = length;
                }
                var start = (points[1] - points[0]).normalized;
                var end = (points[points.Length - 1] - points[points.Length - 2]).normalized;
                if (start.sqrMagnitude < 0.001f) start = Vector3.forward;
                if (end.sqrMagnitude < 0.001f) end = Vector3.forward;

                var lanes = Mathf.Max(1, source.Lanes);
                var traffic = new List<VehicleState>[lanes];
                for (var i = 0; i < lanes; i++) traffic[i] = new List<VehicleState>();
                return new DirectedRoad
                {
                    Traffic = traffic,
                    Key = $"{source.Id}|{direction}",
                    RoadId = source.Id,
                    Class = source.Class,
                    Lanes = lanes,
                    SpeedMps = Mathf.Max(2f, source.SpeedKph / 3.6f),
                    Oneway = source.Oneway,
                    FromNode = direction == 1 ? source.FromNode : source.ToNode,
                    ToNode = direction == 1 ? source.ToNode : source.FromNode,
                    Points = points,
                    Along = along,
                    Length = length,
                    StartDirection = start,
                    EndDirection = end,
                };
            }

            // Metres to the left of travel, as the backend places lanes: Singapore drives on the left.
            public float LaneOffset(int lane)
            {
                lane = Mathf.Clamp(lane, 0, Mathf.Max(0, Lanes - 1));
                return Oneway ? (lane - (Lanes - 1) * 0.5f) * LaneWidthM : (lane + 0.5f) * LaneWidthM;
            }

            public int LaneFrom(int lane) => Mathf.Clamp(lane, 0, Lanes - 1);

            // How far a point is from the centreline, and how far along it and which way it runs there.
            public float Project(Vector2 point, out float along, out Vector3 tangent)
            {
                var best = float.MaxValue;
                along = 0;
                tangent = StartDirection;
                for (var i = 1; i < Points.Length; i++)
                {
                    var a = new Vector2(Points[i - 1].x, Points[i - 1].z);
                    var run = new Vector2(Points[i].x, Points[i].z) - a;
                    var share = run.sqrMagnitude > 0 ? Mathf.Clamp01(Vector2.Dot(point - a, run) / run.sqrMagnitude) : 0;
                    var distance = Vector2.Distance(point, a + run * share);
                    if (distance >= best) continue;
                    best = distance;
                    along = Along[i - 1] + (Along[i] - Along[i - 1]) * share;
                    tangent = (Points[i] - Points[i - 1]).normalized;
                }
                return best;
            }

            public bool Sample(float distance, float leftM, out Vector3 position, out Vector3 tangent)
            {
                position = default;
                tangent = Vector3.forward;
                if (Points == null || Points.Length < 2 || Length <= 0) return false;

                var d = Mathf.Clamp(distance, 0, Length);
                var index = 1;
                while (index < Along.Length && Along[index] < d) index++;
                index = Mathf.Min(index, Along.Length - 1);
                var from = Points[index - 1];
                var to = Points[index];
                var segment = Mathf.Max(0.001f, Along[index] - Along[index - 1]);
                var t = Mathf.Clamp01((d - Along[index - 1]) / segment);
                var center = Vector3.Lerp(from, to, t);
                tangent = (to - from).normalized;
                if (tangent.sqrMagnitude < 0.001f) tangent = Vector3.forward;
                // Unity is left-handed: forward x up points to the left of travel
                var left = Vector3.Cross(tangent, Vector3.up).normalized;
                position = center + left * leftM;
                return true;
            }
        }
    }
}
