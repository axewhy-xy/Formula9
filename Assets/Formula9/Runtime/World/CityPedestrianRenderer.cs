using System;
using System.Collections.Generic;
using System.Linq;
using Formula9.Backend;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Formula9.World
{
    // Pedestrians that stay on sidewalks and cross roads only at known crossings.
    public sealed class CityPedestrianRenderer : IDisposable
    {
        private const float PedestrianY = RoadMesh.SurfaceY;
        private const float GroundNudgeY = -0.06f;
        private const float CrossingEndOffsetM = 1.1f;
        private const float MergeNodeM = 1.2f;
        private const float MinSidewalkLinkM = 3.5f;
        private const float MaxSidewalkLinkM = 32f;
        private const int MaxSidewalkLinksPerNode = 4;
        private const float SignalPollS = 0.25f;
        private const float RoadSurfaceEpsilonM = 0.15f;
        private const int TargetPedestrians = 1000;
        private const int MaxAnimatedPedestrians = 180;
        private const float CrowdCellM = 1.3f;
        private const float PersonalSpaceM = 0.52f;
        private const float MinPauseS = 1.2f;
        private const float MaxPauseS = 5.5f;

        private readonly Transform root;
        private readonly List<RoadSection> roadSections = new List<RoadSection>();
        private readonly List<PedestrianNode> nodes = new List<PedestrianNode>();
        private readonly List<PedestrianEdge> edges = new List<PedestrianEdge>();
        private readonly List<List<int>> outgoing = new List<List<int>>();
        private readonly List<PedestrianModel> models = new List<PedestrianModel>();
        private readonly List<PedestrianAgent> agents = new List<PedestrianAgent>();
        private readonly List<int> activeNodes = new List<int>();
        private readonly Dictionary<long, List<int>> crowdCells = new Dictionary<long, List<int>>();
        private readonly System.Random random = new System.Random(19);
        private CitySignalData signalData;
        private Func<double> signalClock = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        private int animatedPedestrians;
        private double lastSignalClock;
        private float stalledSignalClockS;

        private struct RoadSection
        {
            public string RoadId;
            public int SegmentIndex;
            public Vector2 A, B;
            public float HalfWidth;
        }

        private sealed class PedestrianNode
        {
            public Vector2 Position;
            public bool NearCrossing;
            public bool Sidewalk;
        }

        private sealed class PedestrianEdge
        {
            public int From, To;
            public float Length;
            public bool IsCrossing, SignalControlled;
            public int OffsetS, Phase;
        }

        private sealed class PedestrianModel
        {
            public GameObject Prefab;
            public AnimationClip Walk;
        }

        private sealed class PedestrianAgent
        {
            public Transform Visual;
            public int Node, PreviousNode = -1;
            public int Edge = -1;
            public float Along;
            public float SpeedMps;
            public float WaitS;
            public float GroundOffset;
            public float TurnLerp;
            public Vector2 Facing = Vector2.right;
            public int SidewalkStepsBeforeCrossing;
            public PlayableGraph Graph;
            public bool HasGraph;
        }

        public CityPedestrianRenderer(Transform parent)
        {
            root = new GameObject("Pedestrians").transform;
            root.SetParent(parent, false);
        }

        public void SetSignalClock(Func<double> clock) =>
            signalClock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);

        public void Rebuild(string area, IReadOnlyList<CityRoad> roads, CitySignalData signals)
        {
            signalData = signals;
            lastSignalClock = signalClock();
            stalledSignalClockS = 0;
            BuildRoadSections(roads);
            BuildGraph(area);
            LoadModels();
            RebuildAgents();
            Debug.Log($"[Pedestrians] Rebuild area={area}, nodes={nodes.Count}, edges={edges.Count}, models={models.Count}, agents={agents.Count}");
        }

        public void Tick(float deltaTime)
        {
            if (agents.Count == 0) return;
            var signalNow = signalClock();
            if (Math.Abs(signalNow - lastSignalClock) > 1e-4)
            {
                stalledSignalClockS = 0;
                lastSignalClock = signalNow;
            }
            else stalledSignalClockS += Mathf.Max(0, deltaTime);
            foreach (var agent in agents)
            {
                if (agent.Visual == null || nodes.Count == 0) continue;
                if (agent.Edge < 0 && !ChooseNextEdge(agent))
                {
                    WarpToActiveNode(agent);
                    continue;
                }
                if (agent.Edge < 0) continue;

                var edge = edges[agent.Edge];
                if (edge.IsCrossing && edge.SignalControlled && !CanCross(edge))
                {
                    agent.WaitS = SignalPollS;
                    PlaceOnNode(agent);
                    continue;
                }
                if (agent.WaitS > 0)
                {
                    agent.WaitS = Mathf.Max(0, agent.WaitS - deltaTime);
                    PlaceOnNode(agent);
                    continue;
                }

                var step = edge.Length <= 0 ? 1f : agent.SpeedMps * Mathf.Max(0, deltaTime) / edge.Length;
                agent.Along = Mathf.Clamp01(agent.Along + step);

                var from = nodes[edge.From].Position;
                var to = nodes[edge.To].Position;
                var at = Vector2.Lerp(from, to, agent.Along);
                var direction = to - from;
                if (direction.sqrMagnitude > 0.0001f) agent.Facing = direction.normalized;
                Place(agent, at, agent.Facing, deltaTime);

                if (agent.Along < 1f) continue;
                agent.PreviousNode = edge.From;
                agent.Node = edge.To;
                agent.Edge = -1;
                agent.Along = 0;
                if (edge.IsCrossing)
                    agent.SidewalkStepsBeforeCrossing = random.Next(4, 12);
                else if (agent.SidewalkStepsBeforeCrossing > 0)
                    agent.SidewalkStepsBeforeCrossing--;
                if (!edge.IsCrossing && random.NextDouble() < 0.08)
                    agent.WaitS = Mathf.Lerp(MinPauseS, MaxPauseS, (float)random.NextDouble());
            }
            ResolveCrowdCollisions();
        }

        public void Dispose()
        {
            ClearAgents();
            if (root != null) WorldBuilder.Remove(root.gameObject);
        }

        private void BuildRoadSections(IReadOnlyList<CityRoad> roads)
        {
            roadSections.Clear();
            if (roads == null) return;
            foreach (var road in roads)
            {
                if (road?.Points == null || road.Points.Count < 2) continue;
                var half = RoadMesh.HalfWidth(road);
                for (var i = 1; i < road.Points.Count; i++)
                {
                    var a = new Vector2((float)road.Points[i - 1].X, (float)road.Points[i - 1].Y);
                    var b = new Vector2((float)road.Points[i].X, (float)road.Points[i].Y);
                    if ((b - a).sqrMagnitude < 0.01f) continue;
                    roadSections.Add(new RoadSection { RoadId = road.Id ?? string.Empty, SegmentIndex = i - 1, A = a, B = b, HalfWidth = half });
                }
            }
        }

        private void BuildGraph(string area)
        {
            nodes.Clear();
            edges.Clear();
            outgoing.Clear();
            activeNodes.Clear();

            var crossings = LoadCrossings(area);
            var crossingNodes = new List<int>();
            foreach (var crossing in crossings)
            {
                if ((crossing.B - crossing.A).sqrMagnitude < 1f) continue;

                var center = (crossing.A + crossing.B) * 0.5f;
                var nearA = crossing.A + (crossing.A - center).normalized * CrossingEndOffsetM;
                var nearB = crossing.B + (crossing.B - center).normalized * CrossingEndOffsetM;
                var nodeA = NodeFor(nearA, nearCrossing: true);
                var nodeB = NodeFor(nearB, nearCrossing: true);
                crossingNodes.Add(nodeA);
                crossingNodes.Add(nodeB);

                int phase = 0, offset = 0;
                var hasSignal = crossing.SignalControlled &&
                    MatchSignal(crossing.A, crossing.B, signalData, out phase, out offset);
                AddBidirectional(nodeA, nodeB, true, hasSignal, offset, phase);
            }

            BuildRoadsideNodes();
            ConnectCrossingsToSidewalk(crossingNodes);
            LinkSidewalkNodes();
        }

        private void BuildRoadsideNodes()
        {
            if (roadSections.Count == 0) return;
            var stride = Mathf.Max(1, roadSections.Count / 120);
            var sampledPerRoad = new Dictionary<string, int>(StringComparer.Ordinal);
            var leftNodeByRoad = new Dictionary<string, int>(StringComparer.Ordinal);
            var rightNodeByRoad = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var road in roadSections)
            {
                var run = road.B - road.A;
                if (run.sqrMagnitude < 0.01f) continue;
                var key = string.IsNullOrEmpty(road.RoadId) ? "__" + road.SegmentIndex : road.RoadId;
                sampledPerRoad.TryGetValue(key, out var sampleCount);
                sampleCount++;
                sampledPerRoad[key] = sampleCount;
                if ((sampleCount - 1) % stride != 0) continue;

                var direction = run.normalized;
                var normal = new Vector2(-direction.y, direction.x);
                var middle = (road.A + road.B) * 0.5f;
                var offset = road.HalfWidth + 2.6f;
                var left = NodeFor(middle + normal * offset, sidewalk: true);
                var right = NodeFor(middle - normal * offset, sidewalk: true);

                if (leftNodeByRoad.TryGetValue(key, out var previousLeft) &&
                    OutsideRoad(nodes[previousLeft].Position, nodes[left].Position))
                    AddBidirectional(previousLeft, left, false, false, 0, 0);
                if (rightNodeByRoad.TryGetValue(key, out var previousRight) &&
                    OutsideRoad(nodes[previousRight].Position, nodes[right].Position))
                    AddBidirectional(previousRight, right, false, false, 0, 0);

                leftNodeByRoad[key] = left;
                rightNodeByRoad[key] = right;
            }
        }

        private void ConnectCrossingsToSidewalk(List<int> crossingNodes)
        {
            if (crossingNodes == null || crossingNodes.Count == 0) return;
            var linked = new HashSet<int>();
            foreach (var crossingNode in crossingNodes)
            {
                if (crossingNode < 0 || crossingNode >= nodes.Count || !linked.Add(crossingNode)) continue;
                var nearby = new List<(int Node, float Dist)>();
                for (var i = 0; i < nodes.Count; i++)
                {
                    if (i == crossingNode || !nodes[i].Sidewalk) continue;
                    var dist = Vector2.Distance(nodes[crossingNode].Position, nodes[i].Position);
                    if (dist > 40f || dist < 1f) continue;
                    nearby.Add((i, dist));
                }
                nearby.Sort((a, b) => a.Dist.CompareTo(b.Dist));
                var made = 0;
                foreach (var (node, _) in nearby)
                {
                    if (made >= 4) break;
                    if (Linked(crossingNode, node)) continue;
                    if (!OutsideRoad(nodes[crossingNode].Position, nodes[node].Position)) continue;
                    AddBidirectional(crossingNode, node, false, false, 0, 0);
                    made++;
                }
            }
        }

        private void LinkSidewalkNodes()
        {
            // Sidewalk links between nearby curb points, but never through asphalt.
            for (var i = 0; i < nodes.Count; i++)
            {
                if (!nodes[i].Sidewalk) continue;
                var near = new List<(int Node, float Dist)>();
                for (var j = 0; j < nodes.Count; j++)
                {
                    if (i == j || !nodes[j].Sidewalk) continue;
                    var dist = Vector2.Distance(nodes[i].Position, nodes[j].Position);
                    if (dist < MinSidewalkLinkM || dist > MaxSidewalkLinkM) continue;
                    near.Add((j, dist));
                }
                near.Sort((a, b) => a.Dist.CompareTo(b.Dist));
                var links = 0;
                foreach (var option in near)
                {
                    if (links >= MaxSidewalkLinksPerNode) break;
                    if (Linked(i, option.Node)) continue;
                    if (!OutsideRoad(nodes[i].Position, nodes[option.Node].Position)) continue;
                    AddBidirectional(i, option.Node, false, false, 0, 0);
                    links++;
                }
            }
        }

        private static List<CrossingRecord> LoadCrossings(string area)
        {
            var records = new List<CrossingRecord>();
            var asset = LoadCrossingAsset(area);
            if (asset == null) return records;
            try
            {
                var root = JObject.Parse(asset.text);
                var crossings = root["crossings"] as JArray;
                if (crossings == null) return records;
                foreach (var item in crossings)
                {
                    var line = item["line_m"] as JArray;
                    if (line == null || line.Count < 2) continue;
                    var first = line[0] as JArray;
                    var last = line[line.Count - 1] as JArray;
                    if (first == null || last == null || first.Count < 2 || last.Count < 2) continue;
                    records.Add(new CrossingRecord
                    {
                        A = new Vector2((float)first[0], (float)first[1]),
                        B = new Vector2((float)last[0], (float)last[1]),
                        SignalControlled = string.Equals((string)item["control"], "signal", StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            catch (Exception error)
            {
                Debug.LogWarning("[Pedestrians] Could not read crossings data: " + error.Message);
            }
            return records;
        }

        private static TextAsset LoadCrossingAsset(string area)
        {
            if (string.IsNullOrWhiteSpace(area)) return null;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            TextAsset Try(string key)
            {
                if (string.IsNullOrWhiteSpace(key)) return null;
                if (!seen.Add(key)) return null;
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

            reduced = trimmed;
            while (true)
            {
                var underscore = reduced.LastIndexOf('_');
                if (underscore <= 0) break;
                reduced = reduced.Substring(0, underscore);
                var match = Try(reduced);
                if (match != null) return match;
            }

            return null;
        }

        private static bool MatchSignal(Vector2 a, Vector2 b, CitySignalData signals, out int phase, out int offset)
        {
            phase = 0;
            offset = 0;
            if (signals == null) return false;
            var center = (a + b) * 0.5f;
            var crossingDirection = (b - a).normalized;
            var bestScore = float.MaxValue;
            var found = false;
            foreach (var junction in signals.Junctions)
                foreach (var approach in junction.Approaches)
                {
                    var stop = new Vector2(approach.StopX, approach.StopY);
                    var distance = Vector2.Distance(center, stop);
                    if (distance > 45f) continue;
                    var heading = approach.HeadingDeg * Mathf.Deg2Rad;
                    var trafficDirection = new Vector2(Mathf.Cos(heading), Mathf.Sin(heading));
                    var alignment = Mathf.Abs(Vector2.Dot(trafficDirection, crossingDirection)); // 0 means perpendicular (good)
                    if (alignment > 0.6f) continue;
                    var score = distance + alignment * 25f;
                    if (score >= bestScore) continue;
                    bestScore = score;
                    phase = approach.Phase;
                    offset = junction.OffsetS;
                    found = true;
                }
            return found;
        }

        private int NodeFor(Vector2 at, bool nearCrossing = false, bool sidewalk = false)
        {
            for (var i = 0; i < nodes.Count; i++)
                if ((nodes[i].Position - at).sqrMagnitude <= MergeNodeM * MergeNodeM)
                {
                    if (nearCrossing) nodes[i].NearCrossing = true;
                    if (sidewalk) nodes[i].Sidewalk = true;
                    return i;
                }
            var index = nodes.Count;
            nodes.Add(new PedestrianNode { Position = at, NearCrossing = nearCrossing, Sidewalk = sidewalk });
            outgoing.Add(new List<int>());
            return index;
        }

        private void AddBidirectional(int a, int b, bool crossing, bool signal, int offset, int phase)
        {
            AddEdge(a, b, crossing, signal, offset, phase);
            AddEdge(b, a, crossing, signal, offset, phase);
        }

        private void AddEdge(int from, int to, bool crossing, bool signal, int offset, int phase)
        {
            if (from == to) return;
            var length = Vector2.Distance(nodes[from].Position, nodes[to].Position);
            if (length < 0.5f) return;
            var edge = new PedestrianEdge
            {
                From = from,
                To = to,
                Length = length,
                IsCrossing = crossing,
                SignalControlled = signal,
                OffsetS = offset,
                Phase = phase
            };
            outgoing[from].Add(edges.Count);
            edges.Add(edge);
        }

        private bool Linked(int a, int b)
        {
            foreach (var edgeIndex in outgoing[a])
                if (edges[edgeIndex].To == b) return true;
            return false;
        }

        private bool OutsideRoad(Vector2 a, Vector2 b)
        {
            if (roadSections.Count == 0) return true;
            const int Samples = 6;
            for (var i = 1; i < Samples; i++)
            {
                var t = i / (float)Samples;
                var at = Vector2.Lerp(a, b, t);
                if (DistanceToRoadEdge(at) < -RoadSurfaceEpsilonM) return false;
            }
            return true;
        }

        private float DistanceToRoadEdge(Vector2 at)
        {
            var best = float.MaxValue;
            foreach (var section in roadSections)
            {
                var distance = DistanceToSegment(at, section.A, section.B) - section.HalfWidth;
                if (distance < best) best = distance;
            }
            return best;
        }

        private static float DistanceToSegment(Vector2 at, Vector2 a, Vector2 b)
        {
            var run = b - a;
            var len = run.sqrMagnitude;
            var t = len <= 0 ? 0 : Mathf.Clamp01(Vector2.Dot(at - a, run) / len);
            return Vector2.Distance(at, a + run * t);
        }

        private void LoadModels()
        {
            models.Clear();
#if UNITY_EDITOR
            var guids = AssetDatabase.FindAssets("t:Model", new[] { "Assets/Animated Men Characters" });
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) continue;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                var clip = AssetDatabase.LoadAllAssetsAtPath(path)
                    .OfType<AnimationClip>()
                    .FirstOrDefault(c => c != null && c.length > 0.4f && !c.name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase));
                models.Add(new PedestrianModel { Prefab = prefab, Walk = clip });
            }
#endif
            if (models.Count == 0)
                Debug.LogWarning("[Pedestrians] No FBX pedestrian models discovered. Add imported character assets under Assets/Animated Men Characters.");
        }

        private void RebuildAgents()
        {
            ClearAgents();
            if (nodes.Count < 2 || edges.Count == 0) return;
            animatedPedestrians = 0;
            activeNodes.Clear();
            for (var i = 0; i < outgoing.Count; i++)
                if (outgoing[i].Count > 0 && nodes[i].Sidewalk) activeNodes.Add(i);
            if (activeNodes.Count == 0)
                for (var i = 0; i < outgoing.Count; i++)
                    if (outgoing[i].Count > 0) activeNodes.Add(i);
            if (activeNodes.Count == 0) return;

            var wanted = TargetPedestrians;
            for (var i = 0; i < wanted; i++)
            {
                var agent = MakeAgent();
                if (agent == null) continue;
                PlaceOnNode(agent);
                ChooseNextEdge(agent);
                agents.Add(agent);
            }
        }

        private PedestrianAgent MakeAgent()
        {
            var agent = new PedestrianAgent
            {
                Node = PickActiveNode(),
                SpeedMps = Mathf.Lerp(1.05f, 1.95f, (float)random.NextDouble()),
                TurnLerp = Mathf.Lerp(5f, 9f, (float)random.NextDouble()),
                SidewalkStepsBeforeCrossing = random.Next(2, 9),
            };

            if (models.Count > 0)
            {
                var picked = models[random.Next(models.Count)];
                var visual = UnityEngine.Object.Instantiate(picked.Prefab, root, false);
                visual.name = "Pedestrian";
                DisablePhysics(visual);
                foreach (var renderer in visual.GetComponentsInChildren<Renderer>(true))
                {
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                }
                agent.Visual = visual.transform;
                var targetHeight = Mathf.Lerp(1.62f, 1.85f, (float)random.NextDouble());
                agent.GroundOffset = FitToHumanHeight(agent.Visual, targetHeight);
                EnsureCollisionBody(agent.Visual, targetHeight);
                if (picked.Walk != null && animatedPedestrians < MaxAnimatedPedestrians)
                {
                    StartWalkAnimation(agent, picked.Walk, (float)random.NextDouble());
                    animatedPedestrians++;
                }
            }
            else
            {
                var capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                capsule.name = "Pedestrian";
                capsule.transform.SetParent(root, false);
                capsule.transform.localScale = new Vector3(0.5f, 0.9f, 0.5f);
                var renderer = capsule.GetComponent<Renderer>();
                if (renderer != null) renderer.sharedMaterial = new Material(Shader.Find("Standard"));
                var collider = capsule.GetComponent<Collider>();
                if (collider != null) collider.enabled = true;
                agent.Visual = capsule.transform;
                agent.GroundOffset = PedestrianY + 0.9f;
            }
            return agent;
        }

        private static void DisablePhysics(GameObject item)
        {
            foreach (var collider in item.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            foreach (var body in item.GetComponentsInChildren<Rigidbody>(true))
            {
                body.isKinematic = true;
                body.detectCollisions = false;
            }
        }

        private static void EnsureCollisionBody(Transform visual, float heightM)
        {
            if (visual == null) return;
            var capsule = visual.GetComponent<CapsuleCollider>();
            if (capsule == null) capsule = visual.gameObject.AddComponent<CapsuleCollider>();
            capsule.direction = 1;
            capsule.height = Mathf.Clamp(heightM * 0.92f, 1.4f, 2.2f);
            capsule.radius = Mathf.Clamp(heightM * 0.16f, 0.2f, 0.35f);
            capsule.center = new Vector3(0, capsule.height * 0.5f, 0);
            capsule.isTrigger = true;
            capsule.enabled = true;
        }

        private static float FitToHumanHeight(Transform visual, float targetHeightM)
        {
            var renderers = visual.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return PedestrianY + 0.9f;
            var bounds = RenderBounds(renderers);
            var height = Mathf.Max(0.2f, bounds.size.y);
            visual.localScale *= targetHeightM / height;
            bounds = RenderBounds(renderers);
            var groundOffset = PedestrianY - bounds.min.y;
            if (!float.IsFinite(groundOffset) || groundOffset < 0.2f || groundOffset > 5f)
                return PedestrianY + 0.9f;
            return groundOffset;
        }

        private static Bounds RenderBounds(Renderer[] renderers)
        {
            Bounds bounds = default;
            var any = false;
            foreach (var renderer in renderers)
            {
                if (!any)
                {
                    bounds = renderer.bounds;
                    any = true;
                }
                else bounds.Encapsulate(renderer.bounds);
            }
            return bounds;
        }

        private static void StartWalkAnimation(PedestrianAgent agent, AnimationClip clip, float phase)
        {
            if (clip == null || agent.Visual == null) return;
            var animator = agent.Visual.GetComponentInChildren<Animator>();
            if (animator == null) return;
            agent.Graph = PlayableGraph.Create("PedestrianWalk");
            var output = AnimationPlayableOutput.Create(agent.Graph, "Walk", animator);
            var playable = AnimationClipPlayable.Create(agent.Graph, clip);
            playable.SetApplyFootIK(true);
            playable.SetTime(phase * clip.length);
            output.SetSourcePlayable(playable);
            agent.Graph.Play();
            agent.HasGraph = true;
        }

        private bool ChooseNextEdge(PedestrianAgent agent)
        {
            if (agent.Node < 0 || agent.Node >= outgoing.Count) return false;
            var options = outgoing[agent.Node];
            if (options.Count == 0) return false;
            var picks = options.Where(i => edges[i].To != agent.PreviousNode).ToList();
            if (picks.Count == 0) picks = options;
            var sidewalk = picks.Where(i => !edges[i].IsCrossing).ToList();
            if (sidewalk.Count > 0 && (agent.SidewalkStepsBeforeCrossing > 0 || random.NextDouble() < 0.9))
                picks = sidewalk;
            var index = picks[random.Next(picks.Count)];
            agent.Edge = index;
            agent.Along = 0;
            var edge = edges[index];
            if (!edge.IsCrossing) return true;
            if (edge.SignalControlled)
            {
                if (!CanCross(edge)) agent.WaitS = SignalPollS;
                else agent.WaitS = 0.2f;
            }
            else agent.WaitS = Mathf.Lerp(0.45f, 1.25f, (float)random.NextDouble());
            return true;
        }

        private bool CanCross(PedestrianEdge edge)
        {
            if (!edge.SignalControlled || signalData == null) return true;
            // When simulation is paused (session not running), the traffic clock can stop.
            // Keep pedestrians moving by allowing crossing on stale signal clocks.
            if (stalledSignalClockS > 1.5f) return true;
            var color = signalData.Timing.ColorAt(signalClock(), edge.OffsetS, edge.Phase);
            return color == SignalColor.Red;
        }

        private void PlaceOnNode(PedestrianAgent agent)
        {
            if (agent.Node < 0 || agent.Node >= nodes.Count) return;
            Place(agent, nodes[agent.Node].Position, agent.Facing, 0);
        }

        private static void Place(PedestrianAgent agent, Vector2 at, Vector2 direction, float deltaTime)
        {
            if (agent.Visual == null) return;
            var world = new Vector3(at.x, agent.GroundOffset + GroundNudgeY, at.y);
            agent.Visual.position = world;
            if (direction.sqrMagnitude < 0.0001f) return;
            var target = Quaternion.LookRotation(new Vector3(direction.x, 0, direction.y), Vector3.up);
            var share = deltaTime > 0 ? 1f - Mathf.Exp(-agent.TurnLerp * deltaTime) : 1f;
            agent.Visual.rotation = Quaternion.Slerp(agent.Visual.rotation, target, share);
        }

        private int PickActiveNode() => activeNodes.Count == 0 ? random.Next(Mathf.Max(1, nodes.Count)) : activeNodes[random.Next(activeNodes.Count)];

        private void WarpToActiveNode(PedestrianAgent agent)
        {
            if (activeNodes.Count == 0) return;
            agent.Node = PickActiveNode();
            agent.PreviousNode = -1;
            agent.Edge = -1;
            agent.Along = 0;
            PlaceOnNode(agent);
        }

        private void ResolveCrowdCollisions()
        {
            if (agents.Count < 2) return;
            crowdCells.Clear();
            for (var i = 0; i < agents.Count; i++)
            {
                var visual = agents[i].Visual;
                if (visual == null) continue;
                var key = CellKey(visual.position.x, visual.position.z);
                if (!crowdCells.TryGetValue(key, out var list))
                    crowdCells[key] = list = new List<int>();
                list.Add(i);
            }

            for (var i = 0; i < agents.Count; i++)
            {
                var a = agents[i].Visual;
                if (a == null) continue;
                var px = a.position.x;
                var pz = a.position.z;
                var cellX = Mathf.FloorToInt(px / CrowdCellM);
                var cellY = Mathf.FloorToInt(pz / CrowdCellM);
                for (var dx = -1; dx <= 1; dx++)
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        var key = (((long)(cellX + dx)) << 32) ^ (uint)(cellY + dy);
                        if (!crowdCells.TryGetValue(key, out var list)) continue;
                        foreach (var index in list)
                        {
                            if (index <= i) continue;
                            var b = agents[index].Visual;
                            if (b == null) continue;
                            var delta = new Vector2(a.position.x - b.position.x, a.position.z - b.position.z);
                            var sqr = delta.sqrMagnitude;
                            if (sqr <= 1e-6f)
                            {
                                var jitter = UnityEngine.Random.insideUnitCircle * 0.02f;
                                a.position += new Vector3(jitter.x, 0, jitter.y);
                                continue;
                            }
                            var min = PersonalSpaceM * PersonalSpaceM;
                            if (sqr >= min) continue;
                            var distance = Mathf.Sqrt(sqr);
                            var push = (PersonalSpaceM - distance) * 0.5f;
                            var normal = delta / distance;
                            a.position += new Vector3(normal.x * push, 0, normal.y * push);
                            b.position -= new Vector3(normal.x * push, 0, normal.y * push);
                        }
                    }
            }
        }

        private static long CellKey(float x, float z)
        {
            var cellX = Mathf.FloorToInt(x / CrowdCellM);
            var cellY = Mathf.FloorToInt(z / CrowdCellM);
            return (((long)cellX) << 32) ^ (uint)cellY;
        }

        private void ClearAgents()
        {
            foreach (var agent in agents)
            {
                if (agent.HasGraph) agent.Graph.Destroy();
                if (agent.Visual == null) continue;
                if (Application.isPlaying) UnityEngine.Object.Destroy(agent.Visual.gameObject);
                else UnityEngine.Object.DestroyImmediate(agent.Visual.gameObject);
            }
            agents.Clear();
        }

        private struct CrossingRecord
        {
            public Vector2 A, B;
            public bool SignalControlled;
        }
    }
}
