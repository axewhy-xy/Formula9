using System;
using System.Collections.Generic;
using Formula9.Backend;
using UnityEngine;

namespace Formula9.World
{
    // People who walk the walk network from the walkways file (backend src.traffic.import_walkways), for a
    // view whose backend sends no pedestrians of its own. Visual only: the traffic here does not see them.
    //
    // The importer worked out the network, so this only walks it: sidewalks down the middle of each footpath,
    // links round corners, and crossings kerb to kerb. A signalled crossing is only stepped onto while the
    // traffic it cuts across is on red, with time to get over; at one with no light people stop to look first.
    // Walkers start where the file's seeds put them, keep to the left half of the walkway for the way they
    // walk, and pause now and then.
    public sealed class PedestrianLocalSim
    {
        // How far a person travels in one walk cycle
        public const float StrideM = 1.5f;
        private const float CrossingSignalPollS = 0.25f;
        private const float SignalSafetyBufferS = 0.45f;
        // People walk side by side in sub-lanes this wide, keeping left
        private const float SublaneM = 0.75f;
        private const float EdgeClearM = 0.2f;
        // Before stepping off at a crossing with no light, people stop to look for this long
        private const float LookMinS = 0.5f, LookMaxS = 1.5f;
        // Idle pauses at a node: how often, and for how long
        private const double PauseChance = 0.04;
        private const float PauseMinS = 0.8f, PauseMaxS = 3.4f;

        private readonly System.Random random = new System.Random(29);
        private readonly List<Vector2> nodes = new List<Vector2>();
        private readonly List<Edge> edges = new List<Edge>();
        private readonly List<List<int>> outgoing = new List<List<int>>();
        private readonly List<int> walkEdges = new List<int>();
        private readonly List<Agent> agents = new List<Agent>();
        private readonly List<PedestrianPose> poses = new List<PedestrianPose>();
        private readonly List<int> scratchAll = new List<int>();
        private readonly List<int> scratchWalk = new List<int>();
        private readonly List<int> scratchCross = new List<int>();

        private CitySignalData signalData;
        private Func<double> signalClock = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        private Func<Vector2, float> groundHeight;

        private sealed class Edge
        {
            public int From, To, Walkway;
            public Vector2[] Points;
            public float[] Along;
            public float Length, WidthM;
            public int Lanes;
            public bool Crossing;
            public CityCrossingControl Control;
            public int OffsetS, Phase;
        }

        private sealed class Agent
        {
            public PedestrianPose Pose;
            public int Node;
            public int PreviousNode = -1;
            public int Edge = -1;
            public float DistanceM;
            public float SpeedMps;
            public float WaitS;
            // Walkways still to walk before it looks for a crossing again
            public int StepsBeforeCrossing;
            // Whether it has stopped to look before the crossing with no light ahead of it
            public bool Looked;
            // Which of the sub-lanes on its side it keeps to, and how far left of the middle that is
            public float LaneShare;
            public float Aside;
            public Vector2 Facing = Vector2.right;
            public float TurnLerp;
        }

        public int NodeCount => nodes.Count;
        // Walkways walkers may use, each walked either way
        public int WalkwayCount { get; private set; }
        public int CrossingCount { get; private set; }
        // Signalled crossings left out because their light is not among the signals given
        public int CrossingsWithoutLight { get; private set; }
        public int AgentCount => agents.Count;
        // Where everyone is after the last Tick, for PedestrianCrowd to draw
        public IReadOnlyList<PedestrianPose> Poses => poses;

        public void SetSignalClock(Func<double> clock) =>
            signalClock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);

        // The ground's height under a point (x east, y north), once the sidewalks are in; until then people
        // walk at the road's height
        public void SetGroundHeight(Func<Vector2, float> height)
        {
            groundHeight = height;
            for (var i = 0; i < agents.Count; i++)
            {
                var at = agents[i].Pose.Position;
                agents[i].Pose.Position = new Vector3(at.x, GroundAt(new Vector2(at.x, at.z)), at.z);
                poses[i] = agents[i].Pose;
            }
        }

        public void Clear()
        {
            nodes.Clear();
            edges.Clear();
            outgoing.Clear();
            walkEdges.Clear();
            agents.Clear();
            poses.Clear();
            WalkwayCount = 0;
            CrossingCount = 0;
            CrossingsWithoutLight = 0;
        }

        // Takes the walkways file's network and walkers. A signalled crossing needs its junction among the
        // signals (whose offset sets its cycle); without them it is left out, not left free to cross.
        public void Rebuild(CityWalkwayData data, CitySignalData signals)
        {
            Clear();
            signalData = signals;
            if (data == null || data.Walkways.Count == 0 || data.Nodes.Count == 0)
            {
                Debug.Log("[Pedestrians] No walk network, so no one walks.");
                return;
            }

            var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
            if (signals?.Junctions != null)
                foreach (var junction in signals.Junctions)
                    if (junction?.Id != null) offsets[junction.Id] = junction.OffsetS;

            foreach (var node in data.Nodes)
            {
                nodes.Add(new Vector2(node.X, node.Y));
                outgoing.Add(new List<int>());
            }
            // Each walkway's edge along it (From to To), or -1 where it was left out
            var forward = new int[data.Walkways.Count];
            foreach (var way in data.Walkways)
            {
                forward[way.Id] = -1;
                var offset = 0;
                if (way.Control == CityCrossingControl.Signal && !offsets.TryGetValue(way.JunctionId ?? "", out offset))
                {
                    CrossingsWithoutLight++;
                    continue;
                }
                var points = new Vector2[way.PointsM.Length / 2];
                for (var i = 0; i < points.Length; i++) points[i] = new Vector2(way.PointsM[i * 2], way.PointsM[i * 2 + 1]);
                var backward = (Vector2[])points.Clone();
                Array.Reverse(backward);
                forward[way.Id] = AddEdge(way, way.From, way.To, points, offset);
                AddEdge(way, way.To, way.From, backward, offset);
                WalkwayCount++;
                if (way.Kind == CityWalkwayKind.Crossing) CrossingCount++;
            }
            for (var i = 0; i < edges.Count; i++)
                if (!edges[i].Crossing) walkEdges.Add(i);

            foreach (var seed in data.Seeds)
            {
                var along = forward[seed.Walkway];
                if (along < 0 || edges[along].Crossing) continue;
                // Half of them walk it the other way, from its far end
                var reverse = random.NextDouble() < 0.5;
                var edgeIndex = reverse ? along + 1 : along;
                var edge = edges[edgeIndex];
                var agent = new Agent
                {
                    Pose = new PedestrianPose
                    {
                        Rotation = Quaternion.identity,
                        Model = seed.Model,
                        ScaleM = seed.HeightM,
                        WalkPhase = (float)random.NextDouble(),
                    },
                    Node = edge.From,
                    Edge = edgeIndex,
                    DistanceM = Mathf.Clamp(reverse ? edge.Length - seed.DistanceM : seed.DistanceM, 0, edge.Length),
                    SpeedMps = seed.SpeedMps,
                    TurnLerp = Mathf.Lerp(4.5f, 8f, (float)random.NextDouble()),
                    StepsBeforeCrossing = random.Next(0, 8),
                    LaneShare = (float)random.NextDouble(),
                };
                agent.Aside = AsideOn(edge, agent.LaneShare);
                PlaceAlongEdge(agent, edge, 0);
                agents.Add(agent);
                poses.Add(agent.Pose);
            }
            Debug.Log($"[Pedestrians] Walk network {data.Area} ({data.SourceHash}): nodes={nodes.Count}, walkways={WalkwayCount}, " +
                $"crossings={CrossingCount} (importer {data.Report.CrossingWalkways}; {CrossingsWithoutLight} left out with no light here; " +
                $"{data.Report.UnattachedCrossings} with nowhere to land and {data.Report.UnmatchedSignalCrossings} with no light found by the importer), walkers={agents.Count}");
        }

        public void Tick(float deltaTime)
        {
            if (deltaTime <= 0 || agents.Count == 0) return;
            var now = signalClock();

            for (var i = 0; i < agents.Count; i++)
            {
                var agent = agents[i];
                agent.Pose.Moving = false;
                Step(agent, deltaTime, now);
                poses[i] = agent.Pose;
            }
        }

        private void Step(Agent agent, float deltaTime, double now)
        {
            if (agent.WaitS > 0)
            {
                // Only ever set at a node: after arriving, or at the kerb waiting for a light or to look
                agent.WaitS = Mathf.Max(0, agent.WaitS - deltaTime);
                PlaceOnNode(agent, deltaTime);
                return;
            }

            if (agent.Edge < 0 && !ChooseNextEdge(agent))
            {
                WarpToRandomWalkway(agent);
                return;
            }

            var edge = edges[agent.Edge];
            // The kerb is only looked from before stepping off: someone mid-road finishes crossing
            if (edge.Crossing && agent.DistanceM <= 0.001f)
            {
                if (edge.Control == CityCrossingControl.Signal && !CanStartCrossing(edge, agent.SpeedMps, now))
                {
                    agent.WaitS = CrossingSignalPollS;
                    PlaceOnNode(agent, deltaTime);
                    return;
                }
                if (edge.Control == CityCrossingControl.Uncontrolled && !agent.Looked)
                {
                    agent.Looked = true;
                    agent.WaitS = Mathf.Lerp(LookMinS, LookMaxS, (float)random.NextDouble());
                    PlaceOnNode(agent, deltaTime);
                    return;
                }
            }

            agent.DistanceM += agent.SpeedMps * deltaTime;
            agent.Pose.Moving = true;
            agent.Pose.WalkPhase += agent.SpeedMps * deltaTime / StrideM;
            if (agent.DistanceM + 1e-4f < edge.Length)
            {
                PlaceAlongEdge(agent, edge, deltaTime);
                return;
            }

            agent.PreviousNode = edge.From;
            agent.Node = edge.To;
            agent.Edge = -1;
            agent.DistanceM = 0;
            if (edge.Crossing) agent.StepsBeforeCrossing = random.Next(4, 12);
            else if (agent.StepsBeforeCrossing > 0) agent.StepsBeforeCrossing--;
            if (!edge.Crossing && random.NextDouble() < PauseChance)
                agent.WaitS = Mathf.Lerp(PauseMinS, PauseMaxS, (float)random.NextDouble());
            PlaceOnNode(agent, deltaTime);
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

            // Walk the sidewalks for a while, then take a crossing when one is here and wanted
            List<int> pool;
            if (scratchCross.Count > 0 && agent.StepsBeforeCrossing <= 0 &&
                (scratchWalk.Count == 0 || random.NextDouble() < 0.6))
                pool = scratchCross;
            else
                pool = scratchWalk.Count > 0 ? scratchWalk : scratchCross;

            agent.Edge = pool[random.Next(pool.Count)];
            agent.DistanceM = 0;
            agent.Looked = false;
            agent.Aside = AsideOn(edges[agent.Edge], agent.LaneShare);
            return true;
        }

        // A red for the traffic the crossing cuts across, with time left to get over before it turns
        private bool CanStartCrossing(Edge edge, float speedMps, double timeS)
        {
            if (signalData == null) return false;
            var redLeftS = signalData.Timing.RedRemainingS(timeS, edge.OffsetS, edge.Phase);
            return redLeftS >= edge.Length / Mathf.Max(0.4f, speedMps) + SignalSafetyBufferS;
        }

        // How far left of a walkway's middle someone keeps: in one of the sub-lanes on their own (left) side
        private static float AsideOn(Edge edge, float share)
        {
            var mine = Mathf.Max(1, edge.Lanes / 2);
            var lane = Mathf.Min(mine - 1, (int)(share * mine));
            return Mathf.Clamp(SublaneM * (0.5f + lane), 0, Mathf.Max(0, edge.WidthM / 2 - EdgeClearM));
        }

        private void PlaceOnNode(Agent agent, float dt)
        {
            if (agent.Node < 0 || agent.Node >= nodes.Count) return;
            Place(agent, nodes[agent.Node] + Left(agent.Facing) * agent.Aside, agent.Facing, dt);
        }

        private void PlaceAlongEdge(Agent agent, Edge edge, float dt)
        {
            var distance = Mathf.Clamp(agent.DistanceM, 0, edge.Length);
            var segment = 0;
            while (segment + 2 < edge.Along.Length && edge.Along[segment + 1] < distance) segment++;
            var from = edge.Points[segment];
            var to = edge.Points[segment + 1];
            var segmentLength = Mathf.Max(0.001f, edge.Along[segment + 1] - edge.Along[segment]);
            var at = Vector2.Lerp(from, to, Mathf.Clamp01((distance - edge.Along[segment]) / segmentLength));
            var direction = to - from;
            if (direction.sqrMagnitude > 1e-5f) agent.Facing = direction.normalized;
            Place(agent, at + Left(agent.Facing) * agent.Aside, agent.Facing, dt);
        }

        private static Vector2 Left(Vector2 facing) => new Vector2(-facing.y, facing.x);

        private void Place(Agent agent, Vector2 at, Vector2 direction, float dt)
        {
            // The model's feet are at its origin, on the raised sidewalk where there is one
            agent.Pose.Position = new Vector3(at.x, GroundAt(at), at.y);
            if (direction.sqrMagnitude < 1e-5f) return;
            var target = Quaternion.LookRotation(new Vector3(direction.x, 0, direction.y), Vector3.up);
            var share = dt > 0 ? 1f - Mathf.Exp(-agent.TurnLerp * dt) : 1f;
            agent.Pose.Rotation = Quaternion.Slerp(agent.Pose.Rotation, target, share);
        }

        private float GroundAt(Vector2 at) => groundHeight != null ? groundHeight(at) : RoadMesh.SurfaceY;

        private void WarpToRandomWalkway(Agent agent)
        {
            if (walkEdges.Count == 0) return;
            var edge = edges[walkEdges[random.Next(walkEdges.Count)]];
            agent.Node = edge.From;
            agent.PreviousNode = -1;
            agent.Edge = -1;
            agent.DistanceM = 0;
            PlaceOnNode(agent, 0);
        }

        private int AddEdge(CityWalkway way, int from, int to, Vector2[] points, int offsetS)
        {
            var along = new float[points.Length];
            for (var i = 1; i < points.Length; i++)
                along[i] = along[i - 1] + Vector2.Distance(points[i - 1], points[i]);
            var index = edges.Count;
            outgoing[from].Add(index);
            edges.Add(new Edge
            {
                From = from, To = to, Walkway = way.Id, Points = points, Along = along,
                Length = Mathf.Max(0.01f, along[points.Length - 1]), WidthM = way.WidthM, Lanes = way.Lanes,
                Crossing = way.Kind == CityWalkwayKind.Crossing, Control = way.Control, OffsetS = offsetS, Phase = way.Phase,
            });
            return index;
        }
    }
}
