using System;
using System.Collections.Generic;
using System.Linq;
using Formula9.Backend;
using UnityEngine;
using UnityEngine.Rendering;

namespace Formula9.World
{
    // Draws and moves the background traffic sample from /api/traffic/{area}.
    public sealed class CityTrafficRenderer : IDisposable
    {
        private const float LaneWidthM = 3.2f;
        private const int BatchSize = 1023;

        private readonly Transform root;
        private readonly Material material;
        private readonly ICollisionLimiter collision = new PassThroughCollisionLimiter();
        private readonly Dictionary<string, DirectedRoad> roads = new Dictionary<string, DirectedRoad>();
        private readonly Dictionary<long, List<DirectedRoad>> exitsByNode = new Dictionary<long, List<DirectedRoad>>();
        private readonly Dictionary<string, List<ModelRenderData>> modelRenderers = new Dictionary<string, List<ModelRenderData>>();
        private readonly List<VehicleState> vehicles = new List<VehicleState>();

        public CityTrafficRenderer(Transform parent)
        {
            root = new GameObject("Background traffic").transform;
            root.SetParent(parent, false);
            material = new Material(Shader.Find("Formula9/CityBuilding")) { enableInstancing = true };
        }

        public void Rebuild(IReadOnlyList<CityRoad> roadsData, CityTrafficData trafficData)
        {
            roads.Clear();
            exitsByNode.Clear();
            ClearModels();
            vehicles.Clear();

            BuildDirectedRoads(roadsData);
            if (roads.Count == 0 || trafficData == null) return;

            var catalog = Resources.Load<TrafficVehicleCatalog>("Formula9/TrafficVehicleCatalog");
            if (catalog == null)
                Debug.LogWarning("Traffic vehicle catalog is missing, so traffic is drawn as plain blocks.");
            foreach (var pair in trafficData.Models)
                modelRenderers[pair.Key] = ModelsFor(catalog, pair.Key, pair.Value);

            foreach (var seed in trafficData.Vehicles)
                AddVehicle(seed);
        }

        public void Tick(float deltaTime)
        {
            if (vehicles.Count == 0) return;

            if (deltaTime > 0)
                for (var i = 0; i < vehicles.Count; i++)
                    Advance(vehicles[i], deltaTime);

            RenderVehicles();
        }

        public void Dispose()
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(material);
            else UnityEngine.Object.DestroyImmediate(material);
            if (root != null) WorldBuilder.Remove(root.gameObject);
            ClearModels();
        }

        private void ClearModels()
        {
            foreach (var list in modelRenderers.Values)
                foreach (var render in list) render.Dispose();
            modelRenderers.Clear();
        }

        // Imported prefabs keep their own materials. A model with no prefab stays a plain block.
        private List<ModelRenderData> ModelsFor(TrafficVehicleCatalog catalog, string modelId, CityTrafficModel model)
        {
            var made = new List<ModelRenderData>();
            if (catalog != null && catalog.entries != null)
            {
                foreach (var entry in catalog.entries)
                {
                    if (entry == null || entry.modelId != modelId || entry.prefab == null) continue;
                    var built = ModelRenderData.FromPrefab(entry.prefab, Mathf.Max(1f, model.LengthM));
                    if (built != null) made.Add(built);
                }
            }
            if (made.Count == 0)
            {
                if (catalog != null) Debug.LogWarning("No imported vehicle for '" + modelId + "', so that traffic is still a plain block.");
                made.Add(ModelRenderData.FromMesh(BuildMesh(modelId, model), material));
            }
            return made;
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

        private void AddVehicle(CityTrafficVehicle seed)
        {
            if (seed == null || string.IsNullOrEmpty(seed.Model)) return;
            if (!modelRenderers.TryGetValue(seed.Model, out var options) || options.Count == 0) return;
            var render = options[(int)(Hash(seed.Id ?? seed.Model) % (uint)options.Count)];

            if (!roads.TryGetValue($"{seed.Road}|{seed.Direction}", out var road))
                road = roads.Values.FirstOrDefault(r => r.RoadId == seed.Road);
            if (road == null) return;

            var lane = Mathf.Clamp(seed.Lane, 0, Mathf.Max(0, road.Lanes - 1));
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
                Color = ParseColor(seed.Color),
                Render = render
            };
            state.NextRoad = ChooseNextRoad(state, allowUturn: false);
            vehicles.Add(state);
        }

        private void Advance(VehicleState vehicle, float dt)
        {
            var desired = vehicle.Road.SpeedMps * vehicle.SpeedFactor;
            desired = Mathf.Min(desired, TurnSpeed(vehicle));
            desired = Mathf.Min(desired, SignalLimit(vehicle, desired));
            desired = collision.LimitSpeed(vehicle, desired);
            vehicle.Speed = Mathf.MoveTowards(vehicle.Speed, desired, Acceleration(vehicle.Model) * dt);
            vehicle.Distance += vehicle.Speed * dt;

            var safety = 0;
            while (vehicle.Distance > vehicle.Road.Length && safety++ < 6)
            {
                vehicle.Distance -= vehicle.Road.Length;
                var next = vehicle.NextRoad ?? ChooseNextRoad(vehicle, allowUturn: true);
                if (next == null) { vehicle.Distance = Mathf.Min(vehicle.Distance, vehicle.Road.Length); break; }
                vehicle.Road = next;
                vehicle.Lane = Mathf.Clamp(vehicle.Lane, 0, Mathf.Max(0, vehicle.Road.Lanes - 1));
                vehicle.NextRoad = ChooseNextRoad(vehicle, allowUturn: false);
            }
        }

        private float TurnSpeed(VehicleState vehicle)
        {
            if (vehicle.NextRoad == null) return vehicle.Road.SpeedMps * vehicle.SpeedFactor;
            var remaining = vehicle.Road.Length - vehicle.Distance;
            if (remaining > 24f) return vehicle.Road.SpeedMps * vehicle.SpeedFactor;
            var angle = Vector3.Angle(vehicle.Road.EndDirection, vehicle.NextRoad.StartDirection);
            var maxTurn = Mathf.Lerp(3f, vehicle.Road.SpeedMps * vehicle.SpeedFactor, Mathf.InverseLerp(120f, 15f, angle));
            return maxTurn;
        }

        private static float SignalLimit(VehicleState vehicle, float desired) => desired;

        private static float Acceleration(string model) =>
            model == "motorcycle" ? 4f : model == "heavy_truck" ? 1.5f : model == "truck" || model == "bus" ? 2f : 3f;

        private DirectedRoad ChooseNextRoad(VehicleState vehicle, bool allowUturn)
        {
            if (!exitsByNode.TryGetValue(vehicle.Road.ToNode, out var exits) || exits.Count == 0) return null;

            var candidates = exits.Where(road =>
            {
                if (!allowUturn && road.ToNode == vehicle.Road.FromNode && road.RoadId == vehicle.Road.RoadId) return false;
                if ((vehicle.Model == "bus" || vehicle.Model == "truck" || vehicle.Model == "heavy_truck") && road.Class == "service")
                    return false;
                return true;
            }).ToList();

            if (candidates.Count == 0) candidates = exits;
            if (candidates.Count == 1) return candidates[0];

            var weighted = new List<(DirectedRoad Road, float Weight)>(candidates.Count);
            foreach (var candidate in candidates)
            {
                var straightness = Mathf.InverseLerp(180f, 0f, Vector3.Angle(vehicle.Road.EndDirection, candidate.StartDirection));
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

        private void RenderVehicles()
        {
            foreach (var list in modelRenderers.Values)
                foreach (var render in list) render.Reset();
            foreach (var vehicle in vehicles)
            {
                if (!vehicle.Road.Sample(vehicle.Distance, vehicle.Road.LaneOffset(vehicle.Lane), out var position, out var tangent)) continue;
                vehicle.Render.Add(Matrix4x4.TRS(position, Quaternion.LookRotation(tangent, Vector3.up), Vector3.one), vehicle.Color);
            }

            foreach (var list in modelRenderers.Values)
                foreach (var render in list) render.Render();
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

            AddBox(vertices, triangles, colors, Vector3.zero + Vector3.up * (height * 0.28f), new Vector3(width, height * 0.56f, length), Color.white);
            var cabinLength = modelId == "heavy_truck" ? length * 0.35f : length * 0.52f;
            var cabinCenter = new Vector3(0, height * 0.74f, modelId == "motorcycle" ? length * 0.05f : length * 0.1f);
            var cabinSize = new Vector3(width * 0.88f, height * 0.42f, cabinLength);
            AddBox(vertices, triangles, colors, cabinCenter, cabinSize, new Color(0.12f, 0.12f, 0.14f, 1));

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
            private readonly List<Part> parts = new List<Part>();
            private readonly List<Matrix4x4> matrices = new List<Matrix4x4>();
            private readonly Mesh ownedMesh;
            private readonly List<Material> ownedMaterials = new List<Material>();

            private ModelRenderData(Mesh ownedMesh) => this.ownedMesh = ownedMesh;

            public static ModelRenderData FromMesh(Mesh mesh, Material material)
            {
                var data = new ModelRenderData(mesh);
                data.parts.Add(new Part { mesh = mesh, material = material, local = Matrix4x4.identity });
                return data;
            }

            // One hidden copy is measured and thrown away. Every vehicle of this model then shares its
            // meshes and materials, scaled so the model's length matches the traffic sample.
            public static ModelRenderData FromPrefab(GameObject prefab, float lengthM)
            {
                var template = UnityEngine.Object.Instantiate(prefab);
                try
                {
                    foreach (var behaviour in template.GetComponentsInChildren<MonoBehaviour>(true))
                        UnityEngine.Object.DestroyImmediate(behaviour);
                    foreach (var collider in template.GetComponentsInChildren<Collider>(true))
                        UnityEngine.Object.DestroyImmediate(collider);
                    foreach (var body in template.GetComponentsInChildren<Rigidbody>(true))
                        UnityEngine.Object.DestroyImmediate(body);

                    template.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                    var bounds = WorldBounds(template);
                    if (bounds.size.sqrMagnitude < 0.0001f) return null;
                    if (bounds.size.x > bounds.size.z)
                    {
                        template.transform.Rotate(0f, 90f, 0f);
                        bounds = WorldBounds(template);
                    }
                    var length = Mathf.Max(bounds.size.z, 0.01f);
                    template.transform.localScale *= Mathf.Max(0.2f, lengthM) / length;
                    bounds = WorldBounds(template);
                    var pivot = Matrix4x4.TRS(new Vector3(bounds.center.x, bounds.min.y, bounds.center.z), template.transform.rotation, Vector3.one);

                    var data = new ModelRenderData(null);
                    foreach (var renderer in template.GetComponentsInChildren<Renderer>(true))
                    {
                        var mesh = MeshOf(renderer);
                        if (mesh == null) continue;
                        var local = pivot.inverse * renderer.localToWorldMatrix;
                        var materials = renderer.sharedMaterials;
                        var count = Mathf.Max(1, mesh.subMeshCount);
                        for (var submesh = 0; submesh < count; submesh++)
                        {
                            var source = materials != null && submesh < materials.Length && materials[submesh] != null
                                ? materials[submesh]
                                : materials != null && materials.Length > 0 ? materials[0] : null;
                            if (source == null) continue;
                            var copy = new Material(source) { enableInstancing = true };
                            data.ownedMaterials.Add(copy);
                            data.parts.Add(new Part { mesh = mesh, submesh = Mathf.Min(submesh, mesh.subMeshCount - 1), material = copy, local = local });
                        }
                    }
                    return data.parts.Count == 0 ? null : data;
                }
                finally
                {
                    if (template != null) UnityEngine.Object.DestroyImmediate(template);
                }
            }

            public void Add(Matrix4x4 matrix, Color color)
            {
                matrices.Add(matrix);
            }

            public void Reset() => matrices.Clear();

            public void Render()
            {
                if (matrices.Count == 0 || parts.Count == 0) return;
                var placed = new List<Matrix4x4>(matrices.Count);
                foreach (var part in parts)
                {
                    placed.Clear();
                    foreach (var matrix in matrices) placed.Add(matrix * part.local);
                    // The project's building shader has no instancing. Vehicle materials are Standard, which does.
                    var instanced = part.material.shader != null && part.material.shader.name.IndexOf("Standard", StringComparison.Ordinal) >= 0;
                    if (!instanced)
                    {
                        foreach (var matrix in placed)
                            Graphics.DrawMesh(part.mesh, matrix, part.material, 0, null, part.submesh, null, ShadowCastingMode.On, true);
                        continue;
                    }
                    for (var start = 0; start < placed.Count; start += BatchSize)
                    {
                        var count = Mathf.Min(BatchSize, placed.Count - start);
                        Graphics.DrawMeshInstanced(
                            part.mesh,
                            part.submesh,
                            part.material,
                            placed.GetRange(start, count),
                            null,
                            ShadowCastingMode.On,
                            receiveShadows: true,
                            layer: 0,
                            camera: null,
                            LightProbeUsage.Off);
                    }
                }
            }

            public void Dispose()
            {
                DestroyObject(ownedMesh);
                foreach (var copy in ownedMaterials) DestroyObject(copy);
            }

            private static void DestroyObject(UnityEngine.Object item)
            {
                if (item == null) return;
                if (Application.isPlaying) UnityEngine.Object.Destroy(item);
                else UnityEngine.Object.DestroyImmediate(item);
            }

            private static Bounds WorldBounds(GameObject template)
            {
                Bounds bounds = default;
                var any = false;
                foreach (var renderer in template.GetComponentsInChildren<Renderer>(true))
                {
                    if (!any) { bounds = renderer.bounds; any = true; }
                    else bounds.Encapsulate(renderer.bounds);
                }
                return bounds;
            }

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
            }
        }

        private sealed class VehicleState
        {
            public string Id, Model;
            public DirectedRoad Road, NextRoad;
            public int Lane;
            public float Distance, Speed, SpeedFactor;
            public uint Rng;
            public Color Color;
            public ModelRenderData Render;
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

                return new DirectedRoad
                {
                    Key = $"{source.Id}|{direction}",
                    RoadId = source.Id,
                    Class = source.Class,
                    Lanes = Mathf.Max(1, source.Lanes),
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

            public float LaneOffset(int lane)
            {
                lane = Mathf.Clamp(lane, 0, Mathf.Max(0, Lanes - 1));
                return Oneway ? (lane - (Lanes - 1) * 0.5f) * LaneWidthM : (lane + 0.5f) * LaneWidthM;
            }

            public bool Sample(float distance, float laneOffset, out Vector3 position, out Vector3 tangent)
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
                var left = Vector3.Cross(Vector3.up, tangent).normalized;
                position = center + left * laneOffset;
                return true;
            }
        }

        private interface ICollisionLimiter
        {
            float LimitSpeed(VehicleState vehicle, float desiredMps);
        }

        private sealed class PassThroughCollisionLimiter : ICollisionLimiter
        {
            public float LimitSpeed(VehicleState vehicle, float desiredMps) => desiredMps;
        }
    }
}
