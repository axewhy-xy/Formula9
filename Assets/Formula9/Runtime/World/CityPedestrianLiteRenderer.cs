using System;
using System.Collections.Generic;
using Formula9.Backend;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Formula9.World
{
    // Lightweight pedestrian simulation: people who walk the pavements beside the roads and cross a road
    // only at a mapped crossing, and at a signalled crossing only while the traffic it stops is on red.
    // Visual only: the backend and the traffic do not know about them.
    //
    // Drawing stays cheap: each character model is baked once into a few static mesh poses (a walk cycle
    // and an idle pose), and every person is then one instance of a pose, drawn in batches. There are no
    // GameObjects, Animators or skinning per person. Without character models, tall and short boxes are drawn.
    public sealed class CityPedestrianLiteRenderer : IDisposable
    {
        private const float PedestrianY = RoadMesh.SurfaceY;
        // How far outside the kerb the pavement walkway runs, and how often it gets a node
        private const float PavementOffsetM = 2.1f;
        private const float SampleStepM = 12f;
        private const float MergeNodeM = 1.6f;
        private const float RoadSurfaceEpsilonM = 0.15f;
        // The crossing's walking line ends at the kerb, which may sit a little inside the road's width
        private const float CrossingKerbEpsilonM = 1.0f;
        private const float CrossingReachM = 16f;
        private const float DeadEndLinkM = 18f;
        private const int TargetPedestrians = 1000;
        private const float CrossingSignalPollS = 0.25f;
        private const float SignalSafetyBufferS = 0.45f;
        private const float NodeGridM = 12f;
        private const float RoadGridM = 40f;
        // Off: tall and short blue boxes. On: the character models in Assets/Animated Men Characters.
        private static readonly bool UseCharacterModels = false;
        // Tall and short people
        private const float MinHeightM = 1.50f;
        private const float MaxHeightM = 1.90f;
        // Poses baked per walk cycle, and how far a person travels in one cycle
        private const int WalkFrames = 8;
        private const float StrideM = 1.5f;
        // Turn a model that comes out facing the wrong way (the characters are expected to face +Z)
        private const float ModelYawDeg = 0f;
        private const int BatchSize = 1023;
        private const float CullMarginM = 3f;

        private readonly System.Random random = new System.Random(29);
        private readonly List<RoadSection> roadSections = new List<RoadSection>();
        private readonly Dictionary<long, List<int>> roadGrid = new Dictionary<long, List<int>>();
        private readonly List<Node> nodes = new List<Node>();
        private readonly List<Edge> edges = new List<Edge>();
        private readonly List<List<int>> outgoing = new List<List<int>>();
        private readonly Dictionary<long, List<int>> nodeGrid = new Dictionary<long, List<int>>();
        private readonly List<int> walkEdges = new List<int>();
        private readonly List<Agent> agents = new List<Agent>();
        private readonly List<int> scratchAll = new List<int>();
        private readonly List<int> scratchWalk = new List<int>();
        private readonly List<int> scratchCross = new List<int>();

        private CitySignalData signalData;
        private Func<double> signalClock = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        private int crossingEdgeCount;

        private readonly List<Model> models = new List<Model>();
        private readonly List<UnityEngine.Object> ownedObjects = new List<UnityEngine.Object>();
        private readonly List<Plane[]> frustums = new List<Plane[]>();
        private readonly Matrix4x4[] batch = new Matrix4x4[BatchSize];
        private int frustumCount;
        private bool modelsLoaded;
        private bool drawFailed;

        private struct RoadSection
        {
            public Vector2 A, B;
            public float HalfWidth;
        }

        private sealed class Node
        {
            public Vector2 Position;
        }

        private sealed class Edge
        {
            public int From, To;
            public Vector2[] Points;
            public float[] Along;
            public float Length;
            public bool Crossing;
            public bool SignalControlled;
            public int OffsetS;
            public int Phase;
        }

        private sealed class Agent
        {
            public Vector3 Position;
            public Quaternion Rotation = Quaternion.identity;
            public int Model;
            public float ScaleM = 1.7f;
            // Walk cycles completed so far, and whether it moved this tick (otherwise it stands)
            public float WalkPhase;
            public bool Moving;
            public int Node;
            public int PreviousNode = -1;
            public int Edge = -1;
            public float DistanceM;
            public float SpeedMps;
            public float WaitS;
            // Pavement nodes still to walk before it looks for a crossing again
            public int PavementStepsBeforeCrossing;
            public Vector2 Facing = Vector2.right;
            public float TurnLerp;
        }

        private struct CrossingRecord
        {
            public Vector2 A, B;
            public bool Signal;
        }

        // One character: static poses, each made of one mesh per skinned part of the model
        private sealed class Model
        {
            public readonly List<Part> Parts = new List<Part>();
            public int WalkSlots = 1;
            public int IdleSlot = -1;
            public List<Matrix4x4>[] Matrices;
        }

        private sealed class Part
        {
            public Mesh[] Poses;
            public Material[] Materials;
        }

        public CityPedestrianLiteRenderer(Transform parent)
        {
            // Nothing is parented: people are drawn, not placed in the scene
        }

        public void SetSignalClock(Func<double> clock) =>
            signalClock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);

        public void Rebuild(string area, IReadOnlyList<CityRoad> roads, CitySignalData signals)
        {
            signalData = signals;
            ClearAgents();
            nodes.Clear();
            edges.Clear();
            outgoing.Clear();
            nodeGrid.Clear();
            walkEdges.Clear();
            roadSections.Clear();
            roadGrid.Clear();
            crossingEdgeCount = 0;

            if (roads == null || roads.Count == 0)
            {
                Debug.Log("[PedestriansLite] Skipped rebuild: no roads.");
                return;
            }

            BuildRoadSections(roads);
            BuildPavements(roads);
            LinkDeadEnds();
            AttachCrossings(area);
            for (var i = 0; i < edges.Count; i++)
                if (!edges[i].Crossing) walkEdges.Add(i);
            SpawnAgents();
            Debug.Log($"[PedestriansLite] Rebuild area={area}, nodes={nodes.Count}, edges={edges.Count}, crossings={crossingEdgeCount / 2}, agents={agents.Count}");
        }

        public void Tick(float deltaTime)
        {
            if (deltaTime <= 0 || agents.Count == 0) return;
            var now = signalClock();

            for (var i = 0; i < agents.Count; i++)
            {
                var agent = agents[i];
                agent.Moving = false;

                if (agent.WaitS > 0)
                {
                    agent.WaitS = Mathf.Max(0, agent.WaitS - deltaTime);
                    if (agent.Edge < 0) PlaceOnNode(agent, deltaTime);
                    else if (agent.DistanceM > 0.001f) PlaceAlongEdge(agent, edges[agent.Edge], deltaTime);
                    else PlaceOnNode(agent, deltaTime);
                    continue;
                }

                if (agent.Edge < 0 && !ChooseNextEdge(agent))
                {
                    WarpToRandomPavement(agent);
                    continue;
                }

                var edge = edges[agent.Edge];
                // The light is only looked at before stepping off the kerb: someone mid-road finishes crossing
                if (edge.Crossing && edge.SignalControlled && agent.DistanceM <= 0.001f &&
                    !CanStartCrossing(edge, agent.SpeedMps, now))
                {
                    agent.WaitS = CrossingSignalPollS;
                    PlaceOnNode(agent, deltaTime);
                    continue;
                }

                agent.DistanceM += agent.SpeedMps * deltaTime;
                agent.Moving = true;
                agent.WalkPhase += agent.SpeedMps * deltaTime / StrideM;
                if (agent.DistanceM + 1e-4f < edge.Length)
                {
                    PlaceAlongEdge(agent, edge, deltaTime);
                    continue;
                }

                agent.PreviousNode = edge.From;
                agent.Node = edge.To;
                agent.Edge = -1;
                agent.DistanceM = 0;
                if (edge.Crossing) agent.PavementStepsBeforeCrossing = random.Next(4, 12);
                else if (agent.PavementStepsBeforeCrossing > 0) agent.PavementStepsBeforeCrossing--;
                if (!edge.Crossing && random.NextDouble() < 0.04)
                    agent.WaitS = Mathf.Lerp(0.8f, 3.4f, (float)random.NextDouble());
                PlaceOnNode(agent, deltaTime);
            }
        }

        public void Dispose()
        {
            ClearAgents();
            models.Clear();
            modelsLoaded = false;
            for (var i = 0; i < ownedObjects.Count; i++) DestroyObject(ownedObjects[i]);
            ownedObjects.Clear();
        }

        // Called once a frame, after the cameras are placed: draws everyone where Tick left them, for
        // the cameras whose turn it is (all of them when none are given).
        public void Draw(IReadOnlyList<Camera> cameras = null)
        {
            if (drawFailed || agents.Count == 0 || models.Count == 0) return;
            try
            {
                DrawAll(cameras);
            }
            catch (Exception error)
            {
                // Never let the crowd take the 3D view down with it
                drawFailed = true;
                Debug.LogWarning("[PedestriansLite] Drawing stopped: " + error);
            }
        }

        private void DrawAll(IReadOnlyList<Camera> cameras)
        {
            SetFrustums(cameras);
            for (var m = 0; m < models.Count; m++)
                foreach (var list in models[m].Matrices) list.Clear();

            for (var i = 0; i < agents.Count; i++)
            {
                var agent = agents[i];
                if (agent.Model < 0 || agent.Model >= models.Count) continue;
                if (cameras != null && !InView(agent.Position)) continue;
                var model = models[agent.Model];
                var slot = model.IdleSlot;
                if (agent.Moving || slot < 0)
                {
                    var cycle = agent.WalkPhase - Mathf.Floor(agent.WalkPhase);
                    slot = Mathf.Min(model.WalkSlots - 1, (int)(cycle * model.WalkSlots));
                }
                model.Matrices[slot].Add(Matrix4x4.TRS(agent.Position, agent.Rotation, Vector3.one * agent.ScaleM));
            }

            for (var m = 0; m < models.Count; m++)
            {
                var model = models[m];
                for (var slot = 0; slot < model.Matrices.Length; slot++)
                {
                    var matrices = model.Matrices[slot];
                    if (matrices.Count == 0) continue;
                    foreach (var part in model.Parts) DrawPose(part, slot, matrices);
                }
            }
        }

        private void DrawPose(Part part, int slot, List<Matrix4x4> matrices)
        {
            var mesh = part.Poses[slot];
            if (mesh == null) return;
            var subMeshes = Mathf.Min(mesh.subMeshCount, part.Materials.Length);
            for (var sub = 0; sub < subMeshes; sub++)
            {
                var material = part.Materials[sub];
                if (material == null) continue;
                if (!SystemInfo.supportsInstancing)
                {
                    for (var i = 0; i < matrices.Count; i++)
                        Graphics.DrawMesh(mesh, matrices[i], material, 0, null, sub, null, ShadowCastingMode.Off, false);
                    continue;
                }
                for (var start = 0; start < matrices.Count; start += BatchSize)
                {
                    var count = Mathf.Min(BatchSize, matrices.Count - start);
                    matrices.CopyTo(start, batch, 0, count);
                    Graphics.DrawMeshInstanced(mesh, sub, material, batch, count, null,
                        ShadowCastingMode.Off, false, 0, null, LightProbeUsage.Off);
                }
            }
        }

        private void SetFrustums(IReadOnlyList<Camera> cameras)
        {
            frustumCount = 0;
            if (cameras == null) return;
            for (var i = 0; i < cameras.Count; i++)
            {
                var camera = cameras[i];
                if (camera == null || !camera.enabled) continue;
                if (frustumCount == frustums.Count) frustums.Add(new Plane[6]);
                GeometryUtility.CalculateFrustumPlanes(camera, frustums[frustumCount]);
                frustumCount++;
            }
        }

        // Whether any camera that draws this frame can see the point; false when none draws
        private bool InView(Vector3 position)
        {
            for (var f = 0; f < frustumCount; f++)
            {
                var planes = frustums[f];
                var inside = true;
                for (var p = 0; p < planes.Length; p++)
                {
                    if (planes[p].GetDistanceToPoint(position) < -CullMarginM)
                    {
                        inside = false;
                        break;
                    }
                }
                if (inside) return true;
            }
            return false;
        }

        private static void DestroyObject(UnityEngine.Object item)
        {
            if (item == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(item);
            else UnityEngine.Object.DestroyImmediate(item);
        }

        // ---------------------------------------------------------------------------------------------
        // Road surfaces, in a grid so asking whether a point is on asphalt looks at a few sections only

        private void BuildRoadSections(IReadOnlyList<CityRoad> roads)
        {
            foreach (var road in roads)
            {
                if (road?.Points == null || road.Points.Count < 2) continue;
                var half = RoadMesh.HalfWidth(road);
                for (var i = 1; i < road.Points.Count; i++)
                {
                    var a = new Vector2((float)road.Points[i - 1].X, (float)road.Points[i - 1].Y);
                    var b = new Vector2((float)road.Points[i].X, (float)road.Points[i].Y);
                    if ((b - a).sqrMagnitude < 0.01f) continue;
                    var index = roadSections.Count;
                    roadSections.Add(new RoadSection { A = a, B = b, HalfWidth = half });

                    var minX = Mathf.FloorToInt((Mathf.Min(a.x, b.x) - half) / RoadGridM);
                    var maxX = Mathf.FloorToInt((Mathf.Max(a.x, b.x) + half) / RoadGridM);
                    var minY = Mathf.FloorToInt((Mathf.Min(a.y, b.y) - half) / RoadGridM);
                    var maxY = Mathf.FloorToInt((Mathf.Max(a.y, b.y) + half) / RoadGridM);
                    for (var x = minX; x <= maxX; x++)
                        for (var y = minY; y <= maxY; y++)
                        {
                            var key = Pack(x, y);
                            if (!roadGrid.TryGetValue(key, out var list)) roadGrid[key] = list = new List<int>();
                            list.Add(index);
                        }
                }
            }
        }

        // Negative inside a road's asphalt
        private float DistanceToRoadEdge(Vector2 at)
        {
            var best = float.MaxValue;
            if (!roadGrid.TryGetValue(Pack(Mathf.FloorToInt(at.x / RoadGridM), Mathf.FloorToInt(at.y / RoadGridM)), out var list))
                return best;
            for (var i = 0; i < list.Count; i++)
            {
                var section = roadSections[list[i]];
                var distance = DistanceToSegment(at, section.A, section.B) - section.HalfWidth;
                if (distance < best) best = distance;
            }
            return best;
        }

        private bool OutsideRoad(Vector2 a, Vector2 b, float epsilon)
        {
            const int Samples = 6;
            for (var i = 1; i < Samples; i++)
            {
                var at = Vector2.Lerp(a, b, i / (float)Samples);
                if (DistanceToRoadEdge(at) < -epsilon) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------------------------------------
        // Pavements: a walkway on each side of every road, a node about every SampleStepM

        private void BuildPavements(IReadOnlyList<CityRoad> roads)
        {
            foreach (var road in roads)
            {
                if (road?.Points == null || road.Points.Count < 2) continue;
                var centre = new Vector2[road.Points.Count];
                for (var i = 0; i < centre.Length; i++)
                    centre[i] = new Vector2((float)road.Points[i].X, (float)road.Points[i].Y);
                var offset = RoadMesh.HalfWidth(road) + PavementOffsetM;
                WalkLine(OffsetPolyline(centre, +offset));
                WalkLine(OffsetPolyline(centre, -offset));
            }
        }

        // Nodes along the line, linked to each other. A point that falls on another road's asphalt (at a
        // junction) is left out and breaks the chain, so a pavement never runs across a road.
        private void WalkLine(Vector2[] line)
        {
            var cumulative = new float[line.Length];
            for (var i = 1; i < line.Length; i++)
                cumulative[i] = cumulative[i - 1] + Vector2.Distance(line[i - 1], line[i]);
            var total = cumulative[line.Length - 1];
            if (total < 1f) return;

            var steps = Mathf.Max(1, Mathf.CeilToInt(total / SampleStepM));
            var previous = -1;
            var segment = 0;
            for (var k = 0; k <= steps; k++)
            {
                var distance = total * k / steps;
                while (segment + 2 < line.Length && cumulative[segment + 1] < distance) segment++;
                var span = Mathf.Max(0.001f, cumulative[segment + 1] - cumulative[segment]);
                var at = Vector2.Lerp(line[segment], line[segment + 1], Mathf.Clamp01((distance - cumulative[segment]) / span));

                if (DistanceToRoadEdge(at) < -0.1f) { previous = -1; continue; }
                var node = NodeFor(at);
                if (previous >= 0 && previous != node && !Linked(previous, node) &&
                    OutsideRoad(nodes[previous].Position, nodes[node].Position, RoadSurfaceEpsilonM))
                    AddPair(previous, node, false, false, 0, 0);
                previous = node;
            }
        }

        // Ends of pavements, such as at a junction's corners, join the nearest pavement ends round the corner
        private void LinkDeadEnds()
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                if (outgoing[i].Count > 1) continue;
                var near = new List<(int Node, float Dist)>();
                ForNodesNear(nodes[i].Position, DeadEndLinkM, other =>
                {
                    if (other == i) return;
                    var distance = Vector2.Distance(nodes[i].Position, nodes[other].Position);
                    if (distance >= 1f) near.Add((other, distance));
                });
                near.Sort((a, b) => a.Dist.CompareTo(b.Dist));
                var made = 0;
                foreach (var (other, _) in near)
                {
                    if (made >= 2) break;
                    if (Linked(i, other)) continue;
                    if (!OutsideRoad(nodes[i].Position, nodes[other].Position, RoadSurfaceEpsilonM)) continue;
                    AddPair(i, other, false, false, 0, 0);
                    made++;
                }
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Crossings: the only edges that cross a road

        private void AttachCrossings(string area)
        {
            foreach (var crossing in LoadCrossings(area))
            {
                var from = KerbNode(crossing.A);
                var to = KerbNode(crossing.B);
                if (from < 0 || to < 0 || from == to) continue;

                int phase = 0, offset = 0;
                var controlled = crossing.Signal && MatchSignal(crossing.A, crossing.B, signalData, out phase, out offset);

                AddEdge(from, to, new[] { nodes[from].Position, crossing.A, crossing.B, nodes[to].Position }, true, controlled, offset, phase);
                AddEdge(to, from, new[] { nodes[to].Position, crossing.B, crossing.A, nodes[from].Position }, true, controlled, offset, phase);
                crossingEdgeCount += 2;
            }
        }

        // The pavement node a crossing's end steps onto: close, and reached without crossing a road
        private int KerbNode(Vector2 end)
        {
            var best = -1;
            var bestDistance = float.MaxValue;
            ForNodesNear(end, CrossingReachM, node =>
            {
                var distance = Vector2.Distance(nodes[node].Position, end);
                if (distance >= bestDistance) return;
                if (!OutsideRoad(nodes[node].Position, end, CrossingKerbEpsilonM)) return;
                bestDistance = distance;
                best = node;
            });
            return best;
        }

        // ---------------------------------------------------------------------------------------------
        // Walkers

        private void SpawnAgents()
        {
            if (walkEdges.Count == 0) return;
            LoadModels();
            if (models.Count == 0) return;
            for (var i = 0; i < TargetPedestrians; i++)
            {
                // Spread over every pavement: start part-way along a random one
                var edgeIndex = walkEdges[random.Next(walkEdges.Count)];
                var start = edges[edgeIndex];
                var agent = new Agent
                {
                    Model = random.Next(models.Count),
                    ScaleM = Mathf.Lerp(MinHeightM, MaxHeightM, (float)random.NextDouble()),
                    WalkPhase = (float)random.NextDouble(),
                    Node = start.From,
                    Edge = edgeIndex,
                    DistanceM = start.Length * (float)random.NextDouble() * 0.9f,
                    SpeedMps = Mathf.Lerp(1.0f, 1.8f, (float)random.NextDouble()),
                    TurnLerp = Mathf.Lerp(4.5f, 8f, (float)random.NextDouble()),
                    PavementStepsBeforeCrossing = random.Next(0, 8),
                };
                PlaceAlongEdge(agent, start, 0);
                agents.Add(agent);
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Character models: baked once into static poses

        private void LoadModels()
        {
            if (modelsLoaded) return;
            modelsLoaded = true;
            var started = Time.realtimeSinceStartup;

            if (!UseCharacterModels)
            {
                AddModel(BoxModel());
                Debug.Log("[PedestriansLite] Drawing blue boxes (UseCharacterModels is off).");
                return;
            }

            var catalog = Resources.Load<PedestrianModelCatalog>("Formula9/PedestrianModelCatalog");
            if (catalog != null && catalog.entries != null)
            {
                foreach (var entry in catalog.entries)
                {
                    if (entry == null || entry.prefab == null) continue;
                    var walk = entry.walkClip != null ? entry.walkClip : FindClip(entry.prefab, entry.walkClipName, "walk");
                    var idle = FindClip(entry.prefab, null, "idle");
                    AddModel(BakeModel(entry.prefab, walk, idle));
                }
            }
#if UNITY_EDITOR
            if (models.Count == 0)
            {
                foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { "Assets/Animated Men Characters" }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) continue;
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab == null) continue;
                    AddModel(BakeModel(prefab, FindClip(prefab, "Walk", "walk"), FindClip(prefab, null, "idle")));
                }
            }
#endif
            if (models.Count == 0)
            {
                Debug.LogWarning("[PedestriansLite] No character models could be baked; drawing boxes instead.");
                AddModel(BoxModel());
            }
            Debug.Log($"[PedestriansLite] Models ready: {models.Count} in {(Time.realtimeSinceStartup - started) * 1000f:0} ms");
        }

        private void AddModel(Model model)
        {
            if (model == null || model.Parts.Count == 0) return;
            var slots = model.WalkSlots + (model.IdleSlot >= 0 ? 1 : 0);
            model.Matrices = new List<Matrix4x4>[slots];
            for (var i = 0; i < slots; i++) model.Matrices[i] = new List<Matrix4x4>(64);
            models.Add(model);
        }

        // The model's walk (or idle) clip: the named one, else one with the word in its name
        private static AnimationClip FindClip(GameObject prefab, string preferredName, string word)
        {
            if (prefab == null) return null;
            var clips = new List<AnimationClip>();
            var animator = prefab.GetComponentInChildren<Animator>(true);
            if (animator != null && animator.runtimeAnimatorController != null)
                clips.AddRange(animator.runtimeAnimatorController.animationClips);
#if UNITY_EDITOR
            var path = AssetDatabase.GetAssetPath(prefab);
            if (!string.IsNullOrEmpty(path))
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                    if (asset is AnimationClip clip) clips.Add(clip);
#endif
            AnimationClip loose = null;
            foreach (var clip in clips)
            {
                if (clip == null || clip.length < 0.2f || clip.name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(preferredName) && clip.name.Equals(preferredName, StringComparison.OrdinalIgnoreCase)) return clip;
                if (clip.name.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (clip.isLooping) return clip;
                if (loose == null) loose = clip;
            }
            return loose;
        }

        // Poses the model while it stands at the origin, once per walk frame plus an idle pose, and keeps each
        // as a mesh scaled to a height of 1 with its feet on y = 0 and centred on x/z.
        private Model BakeModel(GameObject prefab, AnimationClip walk, AnimationClip idle)
        {
            var instance = UnityEngine.Object.Instantiate(prefab);
            instance.name = "PedestrianBake";
            try
            {
                foreach (var animator in instance.GetComponentsInChildren<Animator>(true)) animator.enabled = false;
                var skinned = instance.GetComponentsInChildren<SkinnedMeshRenderer>(false);
                if (skinned.Length == 0) return null;

                var model = new Model();
                var walkSlots = walk != null ? WalkFrames : 1;
                var slots = walkSlots + (idle != null ? 1 : 0);
                model.WalkSlots = walkSlots;
                model.IdleSlot = idle != null ? walkSlots : -1;
                foreach (var renderer in skinned)
                {
                    if (renderer.sharedMesh == null) continue;
                    model.Parts.Add(new Part { Poses = new Mesh[slots], Materials = InstancedMaterials(renderer.sharedMaterials) });
                }
                if (model.Parts.Count == 0) return null;

                var bounds = default(Bounds);
                var hasBounds = false;
                for (var slot = 0; slot < slots; slot++)
                {
                    if (slot < walkSlots)
                    {
                        if (walk != null) walk.SampleAnimation(instance, walk.length * slot / walkSlots);
                    }
                    else idle.SampleAnimation(instance, 0f);

                    var part = 0;
                    foreach (var renderer in skinned)
                    {
                        if (renderer.sharedMesh == null) continue;
                        var mesh = new Mesh { name = prefab.name + " pose " + slot };
                        ownedObjects.Add(mesh);
                        renderer.BakeMesh(mesh, true);
                        // Into the model's space (it stands at the origin, unscaled)
                        var rotation = renderer.transform.rotation;
                        var vertices = mesh.vertices;
                        var normals = mesh.normals;
                        var tangents = mesh.tangents;
                        for (var v = 0; v < vertices.Length; v++)
                            vertices[v] = rotation * vertices[v] + renderer.transform.position;
                        for (var n = 0; n < normals.Length; n++) normals[n] = rotation * normals[n];
                        for (var t = 0; t < tangents.Length; t++)
                        {
                            var direction = rotation * new Vector3(tangents[t].x, tangents[t].y, tangents[t].z);
                            tangents[t] = new Vector4(direction.x, direction.y, direction.z, tangents[t].w);
                        }
                        mesh.vertices = vertices;
                        if (normals.Length == vertices.Length) mesh.normals = normals;
                        if (tangents.Length == vertices.Length) mesh.tangents = tangents;
                        mesh.RecalculateBounds();
                        if (!hasBounds) { bounds = mesh.bounds; hasBounds = true; }
                        else bounds.Encapsulate(mesh.bounds);
                        model.Parts[part].Poses[slot] = mesh;
                        part++;
                    }
                }

                if (!hasBounds || bounds.size.y < 0.01f || !float.IsFinite(bounds.size.y)) return null;
                var scale = 1f / bounds.size.y;
                var origin = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
                var yaw = Quaternion.Euler(0f, ModelYawDeg, 0f);
                foreach (var part in model.Parts)
                {
                    foreach (var mesh in part.Poses)
                    {
                        if (mesh == null) continue;
                        var vertices = mesh.vertices;
                        for (var v = 0; v < vertices.Length; v++) vertices[v] = yaw * ((vertices[v] - origin) * scale);
                        mesh.vertices = vertices;
                        if (ModelYawDeg != 0f)
                        {
                            var normals = mesh.normals;
                            for (var n = 0; n < normals.Length; n++) normals[n] = yaw * normals[n];
                            mesh.normals = normals;
                        }
                        mesh.RecalculateBounds();
                        mesh.UploadMeshData(true);
                    }
                }
                return model;
            }
            catch (Exception error)
            {
                Debug.LogWarning($"[PedestriansLite] Could not bake {prefab.name}: {error.Message}");
                return null;
            }
            finally
            {
                instance.SetActive(false);
                DestroyObject(instance);
            }
        }

        // The model's own materials, copied with instancing switched on so a crowd draws in a few batches
        private readonly Dictionary<Material, Material> instancedMaterials = new Dictionary<Material, Material>();

        private Material[] InstancedMaterials(Material[] source)
        {
            var result = new Material[source.Length];
            for (var i = 0; i < source.Length; i++)
            {
                var original = source[i];
                if (original == null) continue;
                if (!instancedMaterials.TryGetValue(original, out var copy))
                {
                    copy = new Material(original) { name = original.name + " (pedestrian)", enableInstancing = true };
                    instancedMaterials[original] = copy;
                    ownedObjects.Add(copy);
                }
                result[i] = copy;
            }
            return result;
        }

        // Used when no character model can be baked: a tall thin blue box, scaled to each person's height
        private Model BoxModel()
        {
            var cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            if (cube == null) return null;
            var mesh = UnityEngine.Object.Instantiate(cube);
            ownedObjects.Add(mesh);
            var vertices = mesh.vertices;
            for (var i = 0; i < vertices.Length; i++)
                vertices[i] = new Vector3(vertices[i].x * 0.18f, vertices[i].y + 0.5f, vertices[i].z * 0.18f);
            mesh.vertices = vertices;
            mesh.RecalculateBounds();

            var shader = Shader.Find("Standard") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            if (shader == null) return null;
            var material = new Material(shader) { color = new Color(0.22f, 0.45f, 0.95f, 1f), enableInstancing = true };
            ownedObjects.Add(material);
            var model = new Model();
            model.Parts.Add(new Part { Poses = new[] { mesh }, Materials = new[] { material } });
            return model;
        }

        private bool ChooseNextEdge(Agent agent)
        {
            if (agent.Node < 0 || agent.Node >= outgoing.Count) return false;
            var options = outgoing[agent.Node];
            if (options.Count == 0) return false;

            scratchAll.Clear();
            scratchWalk.Clear();
            scratchCross.Clear();
            for (var i = 0; i < options.Count; i++)
            {
                var index = options[i];
                if (edges[index].To == agent.PreviousNode) continue;
                scratchAll.Add(index);
                (edges[index].Crossing ? scratchCross : scratchWalk).Add(index);
            }
            // A dead end, or only the way it came: turn round
            if (scratchAll.Count == 0)
            {
                for (var i = 0; i < options.Count; i++)
                {
                    var index = options[i];
                    scratchAll.Add(index);
                    (edges[index].Crossing ? scratchCross : scratchWalk).Add(index);
                }
            }

            // Walk the pavement for a while, then take a crossing when one is here and wanted
            List<int> pool;
            if (scratchCross.Count > 0 && agent.PavementStepsBeforeCrossing <= 0 &&
                (scratchWalk.Count == 0 || random.NextDouble() < 0.6))
                pool = scratchCross;
            else
                pool = scratchWalk.Count > 0 ? scratchWalk : scratchCross;

            agent.Edge = pool[random.Next(pool.Count)];
            agent.DistanceM = 0;
            return true;
        }

        // A red for the traffic the crossing cuts across, with time left to get over before it turns
        private bool CanStartCrossing(Edge edge, float speedMps, double timeS)
        {
            if (!edge.SignalControlled || signalData == null) return true;
            var timing = signalData.Timing;
            var phaseS = timing.GreenS + timing.YellowS + timing.AllRedS;
            var cycleS = timing.Phases * phaseS;
            if (phaseS <= 0 || cycleS <= 0) return true;
            var into = (timeS + edge.OffsetS - edge.Phase * phaseS) % cycleS;
            if (into < 0) into += cycleS;
            if (timing.ColorAt(timeS, edge.OffsetS, edge.Phase) != SignalColor.Red) return false;
            var timeLeftS = cycleS - into;
            var needS = edge.Length / Mathf.Max(0.4f, speedMps) + SignalSafetyBufferS;
            return timeLeftS >= needS;
        }

        private void PlaceOnNode(Agent agent, float dt)
        {
            if (agent.Node < 0 || agent.Node >= nodes.Count) return;
            Place(agent, nodes[agent.Node].Position, agent.Facing, dt);
        }

        private void PlaceAlongEdge(Agent agent, Edge edge, float dt)
        {
            if (edge.Points == null || edge.Points.Length < 2)
            {
                PlaceOnNode(agent, dt);
                return;
            }
            var distance = Mathf.Clamp(agent.DistanceM, 0, edge.Length);
            var segment = 0;
            while (segment + 2 < edge.Along.Length && edge.Along[segment + 1] < distance) segment++;
            var from = edge.Points[segment];
            var to = edge.Points[segment + 1];
            var segmentLength = Mathf.Max(0.001f, edge.Along[segment + 1] - edge.Along[segment]);
            var at = Vector2.Lerp(from, to, Mathf.Clamp01((distance - edge.Along[segment]) / segmentLength));
            var direction = to - from;
            if (direction.sqrMagnitude > 1e-5f) agent.Facing = direction.normalized;
            Place(agent, at, agent.Facing, dt);
        }

        private static void Place(Agent agent, Vector2 at, Vector2 direction, float dt)
        {
            // The model's feet are at its origin
            agent.Position = new Vector3(at.x, PedestrianY, at.y);
            if (direction.sqrMagnitude < 1e-5f) return;
            var target = Quaternion.LookRotation(new Vector3(direction.x, 0, direction.y), Vector3.up);
            var share = dt > 0 ? 1f - Mathf.Exp(-agent.TurnLerp * dt) : 1f;
            agent.Rotation = Quaternion.Slerp(agent.Rotation, target, share);
        }

        private void WarpToRandomPavement(Agent agent)
        {
            if (walkEdges.Count == 0) return;
            var edge = edges[walkEdges[random.Next(walkEdges.Count)]];
            agent.Node = edge.From;
            agent.PreviousNode = -1;
            agent.Edge = -1;
            agent.DistanceM = 0;
            PlaceOnNode(agent, 0);
        }

        // ---------------------------------------------------------------------------------------------
        // Graph helpers

        private void AddPair(int a, int b, bool crossing, bool signalControlled, int offsetS, int phase)
        {
            var forward = new[] { nodes[a].Position, nodes[b].Position };
            var backward = new[] { nodes[b].Position, nodes[a].Position };
            AddEdge(a, b, forward, crossing, signalControlled, offsetS, phase);
            AddEdge(b, a, backward, crossing, signalControlled, offsetS, phase);
        }

        private void AddEdge(int from, int to, Vector2[] points, bool crossing, bool signalControlled, int offsetS, int phase)
        {
            if (from == to || points == null || points.Length < 2) return;
            var along = new float[points.Length];
            for (var i = 1; i < points.Length; i++)
                along[i] = along[i - 1] + Vector2.Distance(points[i - 1], points[i]);
            var length = along[points.Length - 1];
            if (length < 0.5f) return;
            outgoing[from].Add(edges.Count);
            edges.Add(new Edge
            {
                From = from, To = to, Points = points, Along = along, Length = length,
                Crossing = crossing, SignalControlled = signalControlled, OffsetS = offsetS, Phase = phase
            });
        }

        private bool Linked(int from, int to)
        {
            var list = outgoing[from];
            for (var i = 0; i < list.Count; i++)
                if (edges[list[i]].To == to) return true;
            return false;
        }

        private int NodeFor(Vector2 at)
        {
            var best = -1;
            var bestSqr = MergeNodeM * MergeNodeM;
            ForNodesNear(at, MergeNodeM, node =>
            {
                var sqr = (nodes[node].Position - at).sqrMagnitude;
                if (sqr > bestSqr) return;
                bestSqr = sqr;
                best = node;
            });
            if (best >= 0) return best;

            var created = nodes.Count;
            nodes.Add(new Node { Position = at });
            outgoing.Add(new List<int>());
            var key = Pack(Mathf.FloorToInt(at.x / NodeGridM), Mathf.FloorToInt(at.y / NodeGridM));
            if (!nodeGrid.TryGetValue(key, out var bucket)) nodeGrid[key] = bucket = new List<int>();
            bucket.Add(created);
            return created;
        }

        private void ForNodesNear(Vector2 at, float radius, Action<int> visit)
        {
            var span = Mathf.CeilToInt(radius / NodeGridM);
            var cellX = Mathf.FloorToInt(at.x / NodeGridM);
            var cellY = Mathf.FloorToInt(at.y / NodeGridM);
            for (var dx = -span; dx <= span; dx++)
                for (var dy = -span; dy <= span; dy++)
                {
                    if (!nodeGrid.TryGetValue(Pack(cellX + dx, cellY + dy), out var list)) continue;
                    for (var i = 0; i < list.Count; i++) visit(list[i]);
                }
        }

        private static long Pack(int x, int y) => ((long)x << 32) ^ (uint)y;

        private static Vector2[] OffsetPolyline(Vector2[] source, float offset)
        {
            var result = new Vector2[source.Length];
            for (var i = 0; i < source.Length; i++)
            {
                var back = i > 0 ? source[i] - source[i - 1] : source[Mathf.Min(1, source.Length - 1)] - source[0];
                var ahead = i + 1 < source.Length ? source[i + 1] - source[i] : source[source.Length - 1] - source[Mathf.Max(0, source.Length - 2)];
                var tangent = back.normalized + ahead.normalized;
                if (tangent.sqrMagnitude < 1e-5f) tangent = ahead.sqrMagnitude > 1e-5f ? ahead : back;
                if (tangent.sqrMagnitude < 1e-5f) tangent = Vector2.right;
                tangent.Normalize();
                result[i] = source[i] + new Vector2(-tangent.y, tangent.x) * offset;
            }
            return result;
        }

        private static float DistanceToSegment(Vector2 at, Vector2 a, Vector2 b)
        {
            var run = b - a;
            var length = run.sqrMagnitude;
            var t = length <= 0 ? 0 : Mathf.Clamp01(Vector2.Dot(at - a, run) / length);
            return Vector2.Distance(at, a + run * t);
        }

        // ---------------------------------------------------------------------------------------------
        // Crossing data and the signal that governs a crossing

        // The signal approach whose traffic the crossing cuts across: nearest one running across the walking line
        private static bool MatchSignal(Vector2 a, Vector2 b, CitySignalData signals, out int phase, out int offset)
        {
            phase = 0;
            offset = 0;
            if (signals == null || signals.Junctions == null) return false;
            var centre = (a + b) * 0.5f;
            var crossingDirection = (b - a).normalized;
            var best = float.MaxValue;
            var found = false;
            // Strict pass first (traffic across the crossing); then any nearby approach, so a signalled
            // crossing is never left free to cross whatever the lights show
            for (var pass = 0; pass < 2 && !found; pass++)
            {
                for (var j = 0; j < signals.Junctions.Count; j++)
                {
                    var junction = signals.Junctions[j];
                    if (junction?.Approaches == null) continue;
                    for (var k = 0; k < junction.Approaches.Count; k++)
                    {
                        var approach = junction.Approaches[k];
                        var distance = Vector2.Distance(centre, new Vector2(approach.StopX, approach.StopY));
                        var limit = pass == 0 ? 45f : 70f;
                        if (distance > limit) continue;
                        var heading = approach.HeadingDeg * Mathf.Deg2Rad;
                        var alignment = Mathf.Abs(Vector2.Dot(new Vector2(Mathf.Cos(heading), Mathf.Sin(heading)), crossingDirection));
                        if (pass == 0 && alignment > 0.6f) continue;
                        var score = distance + (pass == 0 ? alignment * 25f : 0f);
                        if (score >= best) continue;
                        best = score;
                        phase = approach.Phase;
                        offset = junction.OffsetS;
                        found = true;
                    }
                }
            }
            return found;
        }

        private static TextAsset CrossingAsset(string area)
        {
            if (string.IsNullOrWhiteSpace(area)) return null;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            TextAsset Try(string key)
            {
                if (string.IsNullOrWhiteSpace(key) || !seen.Add(key)) return null;
                return Resources.Load<TextAsset>("Formula9/RoadData/" + key + "-crossings");
            }

            var trimmed = area.Trim();
            var exact = Try(trimmed);
            if (exact != null) return exact;

            var reduced = trimmed;
            while (true)
            {
                var dash = reduced.LastIndexOf('-');
                if (dash <= 0) break;
                reduced = reduced.Substring(0, dash);
                var match = Try(reduced);
                if (match != null) return match;
            }
            return null;
        }

        private static List<CrossingRecord> LoadCrossings(string area)
        {
            var results = new List<CrossingRecord>();
            var asset = CrossingAsset(area);
            if (asset == null) return results;
            try
            {
                var parsed = JObject.Parse(asset.text);
                var crossings = parsed["crossings"] as JArray;
                if (crossings == null) return results;
                foreach (var token in crossings)
                {
                    var line = token["line_m"] as JArray;
                    if (line == null || line.Count < 2) continue;
                    var first = line[0] as JArray;
                    var last = line[line.Count - 1] as JArray;
                    if (first == null || last == null || first.Count < 2 || last.Count < 2) continue;
                    results.Add(new CrossingRecord
                    {
                        A = new Vector2((float)first[0], (float)first[1]),
                        B = new Vector2((float)last[0], (float)last[1]),
                        Signal = string.Equals((string)token["control"], "signal", StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            catch (Exception error)
            {
                Debug.LogWarning("[PedestriansLite] Could not read crossings: " + error.Message);
            }
            return results;
        }

        private void ClearAgents()
        {
            agents.Clear();
        }
    }
}
