using System;
using System.Collections.Generic;
using Formula9.Backend;
using UnityEngine;

namespace Formula9.World
{
    // Part of a road traffic must keep off, as a line in local metres. Unless it is both ways, the line
    // runs the way the traffic it holds up is going.
    public sealed class TrafficHazard
    {
        public Vector2[] Path;
        public bool BothWays;
    }

    // A vehicle the traffic does not move, and so has to keep clear of: a shuttle.
    public struct TrafficObstacle
    {
        public string Id;
        public Vector3 Position, Forward;
        public float HalfLength, HalfWidth;
    }

    public readonly struct TrafficReport
    {
        public readonly int Driving, Outside, Overlaps, RedLightRuns;
        public readonly float MeanSpeedMps;

        public TrafficReport(int driving, int outside, int overlaps, int redLightRuns, float meanSpeedMps)
        {
            Driving = driving; Outside = outside; Overlaps = overlaps; RedLightRuns = redLightRuns; MeanSpeedMps = meanSpeedMps;
        }
    }

    // How the traffic drives. Each step, in order: vehicles a hazard has caught are moved away, every
    // vehicle looks along the roads it plans to take and asks for the next junction, the junctions answer,
    // and every vehicle picks a speed from the same picture of the road, so none reacts to a move another
    // made in the same step.
    public sealed partial class CityTrafficRenderer
    {
        // How far ahead a vehicle has chosen its roads
        private const float PlanAheadM = 80f;
        private const int MaxPlannedRoads = 8;
        // The Krauss car-following model: a speed from which the vehicle can always stop in time, whatever
        // the vehicle in front does, so long as a step is no longer than the reaction time.
        private const float ReactionS = 1f, ComfortBrake = 3.5f, HardBrake = 9f;
        // A driver stops for a yellow if this firm a stop will do. Anyone closer has time to clear the line.
        private const float YellowBrake = 6f;
        private const float MinGapM = 2f, StopMarginM = 0.5f, NodeStopBackM = 3f;
        // A front this far past a stop line has crossed it, and the light no longer applies
        private const float PastLineM = 1.5f;
        // Asks for a junction this far beyond its braking distance, and is seen by others from further off
        private const float ClaimMarginM = 8f, NoticeS = 5f;
        // A rear this far past the junction has left it
        private const float ClearedJunctionM = 2f;
        // Movements through a junction are compared along this much road either side of it, this close together meaning they conflict
        private const float MovementReachM = 6f, ConflictM = 2.2f;
        // Waiting this long counts for one road class, so a side road gets out in the end
        private const float WaitPerRankS = 5f, SignalRank = 10f;
        // A leader slower than this is standing, so the road has no room behind it
        private const float StandingMps = 2f, StoppedMps = 0.3f;
        // Stopped this long, a vehicle gives back a junction it has not entered, for one blocked behind it to go
        private const float ReleaseClaimS = 3f;
        // Past any red light's wait: stopped this long is a jam nothing will clear, so the vehicle leaves the area
        private const float StuckS = 90f;
        // It comes in from off the map no faster than this, and from rest where a light is close by
        private const float EnterMps = 5f;
        private const float HazardMatchM = 5f, HazardStepM = 4f, HazardMinOverlapM = 6f, HazardSnapM = 4f, HazardParallel = 0.7f, HazardCellM = 40f;
        private const float ShuttleSightM = 60f, ShuttleClearanceM = 0.3f, ShuttleBesideM = 0.15f, ShuttleMovingMps = 1f, ShuttlePassBehindM = 8f, ShuttlePassAheadM = 30f;
        private const float MaxAsideM = 3.5f, AsideMps = 1.5f, PassingMps = 5f, ShuttleJunctionM = 25f, StandingShuttleJunctionM = 8f;

        private readonly List<DirectedRoad> filledRoads = new List<DirectedRoad>();
        private readonly List<DirectedRoad> hazardRoads = new List<DirectedRoad>();
        private readonly Dictionary<long, List<Claim>> claimsByNode = new Dictionary<long, List<Claim>>();
        private readonly Dictionary<long, List<NodeRequest>> requestsByNode = new Dictionary<long, List<NodeRequest>>();
        private readonly List<long> askedNodes = new List<long>();
        private readonly List<NodeRequest> denied = new List<NodeRequest>();
        private readonly List<ShuttleState> shuttles = new List<ShuttleState>();
        private readonly Dictionary<(int, int), List<DirectedRoad>> roadGrid = new Dictionary<(int, int), List<DirectedRoad>>();
        private CitySignalData signalData;
        private int redLightRuns;

        // Seconds on the lights' clock, which runs with the traffic. It starts as the backend's does, at the current time.
        public double SignalClockS { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        public TrafficReport Report()
        {
            FillLanes();
            int driving = 0, outside = 0, overlaps = 0;
            var speed = 0f;
            foreach (var vehicle in vehicles)
            {
                if (vehicle.Outside) { outside++; continue; }
                driving++;
                speed += vehicle.Speed;
                var lane = vehicle.Road.Traffic[vehicle.Lane];
                if (vehicle.LaneIndex + 1 < lane.Count && lane[vehicle.LaneIndex + 1].Distance - lane[vehicle.LaneIndex + 1].Length / 2 < vehicle.Distance + vehicle.Length / 2) overlaps++;
            }
            return new TrafficReport(driving, outside, overlaps, redLightRuns, driving > 0 ? speed / driving : 0);
        }

        // Stop lines on the roads, for every light that controls one.
        private void PlaceSignals(CitySignalData signals)
        {
            signalData = signals;
            if (signals == null) return;
            foreach (var junction in signals.Junctions)
                foreach (var approach in junction.Approaches)
                {
                    var heading = approach.HeadingDeg * Mathf.Deg2Rad;
                    var travel = new Vector3(Mathf.Cos(heading), 0, Mathf.Sin(heading));
                    var stop = new Vector2(approach.StopX, approach.StopY);
                    AddStopLine(junction, approach, stop, travel, 1);
                    AddStopLine(junction, approach, stop, travel, -1);
                }
        }

        private void AddStopLine(CitySignalJunction junction, CitySignalApproach approach, Vector2 stop, Vector3 travel, int direction)
        {
            if (!roads.TryGetValue($"{approach.Road}|{direction}", out var road)) return;
            road.Project(stop, out var along, out var tangent);
            // The road's other direction runs past the same place, and has no light here
            if (Vector3.Dot(tangent, travel) < 0.5f) return;
            road.Stops.Add(new StopLine { At = along, OffsetS = junction.OffsetS, Phase = approach.Phase });
        }

        // Replaces what traffic must keep off, and turns vehicles away from roads they had not yet reached.
        public void SetHazards(IReadOnlyList<TrafficHazard> hazards)
        {
            foreach (var road in hazardRoads) road.Hazards.Clear();
            hazardRoads.Clear();
            if (hazards == null || hazards.Count == 0) return;
            if (roadGrid.Count == 0) BuildRoadGrid();
            var overlaps = new Dictionary<DirectedRoad, (float From, float To)>();
            foreach (var hazard in hazards)
            {
                if (hazard?.Path == null || hazard.Path.Length < 2) continue;
                overlaps.Clear();
                var length = IncidentSimulation.Length(hazard.Path);
                for (var along = 0f; ; along = Mathf.Min(along + HazardStepM, length))
                {
                    var at = IncidentSimulation.At(hazard.Path, along, out var heading);
                    NoteOverlaps(at, new Vector3(heading.x, 0, heading.y), hazard.BothWays, overlaps);
                    if (along >= length) break;
                }
                foreach (var pair in overlaps)
                {
                    var road = pair.Key;
                    var span = pair.Value;
                    // Where a road only touches the line, as at a junction, it is not on it
                    if (span.To - span.From < Mathf.Min(HazardMinOverlapM, Mathf.Min(road.Length * 0.5f, length * 0.8f))) continue;
                    if (span.From < HazardSnapM) span.From = 0;
                    if (road.Length - span.To < HazardSnapM) span.To = road.Length;
                    if (road.Hazards.Count == 0) hazardRoads.Add(road);
                    road.Hazards.Add(new Span { From = span.From, To = span.To });
                }
            }
            foreach (var vehicle in vehicles) DropBlockedPlan(vehicle);
        }

        // The shuttles as they stand now. Their speed is worked out from where they were last time.
        public void SetShuttles(IReadOnlyList<TrafficObstacle> obstacles)
        {
            for (var i = shuttles.Count - 1; i >= 0; i--)
            {
                var found = false;
                foreach (var obstacle in obstacles) found |= obstacle.Id == shuttles[i].Pose.Id;
                if (!found) shuttles.RemoveAt(i);
            }
            foreach (var obstacle in obstacles)
            {
                var known = shuttles.Find(s => s.Pose.Id == obstacle.Id);
                if (known == null) shuttles.Add(known = new ShuttleState { Last = obstacle.Position });
                known.Pose = obstacle;
            }
        }

        private void NoteOverlaps(Vector2 at, Vector3 heading, bool bothWays, Dictionary<DirectedRoad, (float From, float To)> overlaps)
        {
            if (!roadGrid.TryGetValue(GridCell(at), out var near)) return;
            foreach (var road in near)
            {
                var distance = road.Project(at, out var along, out var tangent);
                if (distance > HazardMatchM) continue;
                var alike = Vector3.Dot(tangent, heading);
                if ((bothWays ? Mathf.Abs(alike) : alike) < HazardParallel) continue;
                overlaps[road] = overlaps.TryGetValue(road, out var span) ? (Mathf.Min(span.From, along), Mathf.Max(span.To, along)) : (along, along);
            }
        }

        private static (int, int) GridCell(Vector2 at) => (Mathf.FloorToInt(at.x / HazardCellM), Mathf.FloorToInt(at.y / HazardCellM));

        // Each road is listed in every cell within reach of it, so one cell's list is enough to search.
        private void BuildRoadGrid()
        {
            foreach (var road in roads.Values)
                for (var i = 1; i < road.Points.Length; i++)
                {
                    var a = new Vector2(road.Points[i - 1].x, road.Points[i - 1].z);
                    var b = new Vector2(road.Points[i].x, road.Points[i].z);
                    var (minX, minY) = GridCell(Vector2.Min(a, b) - Vector2.one * HazardMatchM);
                    var (maxX, maxY) = GridCell(Vector2.Max(a, b) + Vector2.one * HazardMatchM);
                    for (var x = minX; x <= maxX; x++)
                        for (var y = minY; y <= maxY; y++)
                        {
                            if (!roadGrid.TryGetValue((x, y), out var list)) roadGrid[(x, y)] = list = new List<DirectedRoad>();
                            if (!list.Contains(road)) list.Add(road);
                        }
                }
        }

        private void Drive(float dt)
        {
            SignalClockS += dt;
            foreach (var shuttle in shuttles)
            {
                shuttle.Speed = Vector3.Distance(shuttle.Pose.Position, shuttle.Last) / dt;
                shuttle.Last = shuttle.Pose.Position;
            }
            foreach (var vehicle in vehicles)
            {
                if (vehicle.Outside) EnterFromOutside(vehicle);
                else if (CaughtByHazard(vehicle)) EnterFromOutside(vehicle);
            }
            FillLanes();
            foreach (var vehicle in vehicles)
            {
                if (vehicle.Outside) continue;
                ReleaseClaims(vehicle);
                PlanAhead(vehicle);
                AskForJunction(vehicle);
            }
            AnswerJunctions(dt);
            foreach (var vehicle in vehicles)
                if (!vehicle.Outside) vehicle.TargetSpeed = SafeSpeed(vehicle);
            foreach (var vehicle in vehicles)
                if (!vehicle.Outside) Move(vehicle, dt);
        }

        // A closure or a growing queue that reaches a vehicle takes it off the road: its place in the
        // queue is one of the incident's own cars.
        private static bool CaughtByHazard(VehicleState vehicle)
        {
            foreach (var span in vehicle.Road.Hazards)
                if (vehicle.Distance + vehicle.Length / 2 > span.From + StopMarginM / 2 && vehicle.Distance - vehicle.Length / 2 < span.To) return true;
            return false;
        }

        // Each lane's vehicles, back to front.
        private void FillLanes()
        {
            foreach (var road in filledRoads)
            {
                road.Filled = false;
                foreach (var lane in road.Traffic) lane.Clear();
            }
            filledRoads.Clear();
            foreach (var vehicle in vehicles)
            {
                if (vehicle.Outside) continue;
                if (!vehicle.Road.Filled) { vehicle.Road.Filled = true; filledRoads.Add(vehicle.Road); }
                vehicle.Road.Traffic[vehicle.Lane].Add(vehicle);
            }
            foreach (var road in filledRoads)
                foreach (var lane in road.Traffic)
                {
                    if (lane.Count > 1) lane.Sort(BackToFront);
                    for (var i = 0; i < lane.Count; i++) lane[i].LaneIndex = i;
                }
        }

        private static readonly Comparison<VehicleState> BackToFront = (a, b) =>
        {
            var order = a.Distance.CompareTo(b.Distance);
            return order != 0 ? order : string.CompareOrdinal(a.Id, b.Id);
        };

        private void PlanAhead(VehicleState vehicle)
        {
            var reach = vehicle.Road.Length - vehicle.Distance;
            foreach (var road in vehicle.Ahead) reach += road.Length;
            while (vehicle.Ahead.Count == 0 || (reach < PlanAheadM && vehicle.Ahead.Count < MaxPlannedRoads))
            {
                var last = vehicle.Ahead.Count > 0 ? vehicle.Ahead[vehicle.Ahead.Count - 1] : vehicle.Road;
                var next = ChooseNextRoad(vehicle, last);
                if (next == null) break;
                vehicle.Ahead.Add(next);
                reach += next.Length;
            }
        }

        // Changes its mind about any road it has not been given way onto, from the first that is now blocked.
        private static void DropBlockedPlan(VehicleState vehicle)
        {
            for (var i = 0; i < vehicle.Ahead.Count; i++)
            {
                if (vehicle.Ahead[i].Hazards.Count == 0 || vehicle.HasClaim(vehicle.Ahead[i])) continue;
                vehicle.Ahead.RemoveRange(i, vehicle.Ahead.Count - i);
                return;
            }
        }

        private void ReleaseClaims(VehicleState vehicle)
        {
            for (var i = vehicle.Claims.Count - 1; i >= 0; i--)
            {
                var claim = vehicle.Claims[i];
                var cleared = claim.Entered && (vehicle.Road != claim.To || vehicle.Distance - vehicle.Length / 2 > ClearedJunctionM);
                if (!cleared && (claim.Entered || vehicle.StoppedS < ReleaseClaimS)) continue;
                claimsByNode[claim.Node].Remove(claim);
                vehicle.Claims.RemoveAt(i);
            }
        }

        private void ReleaseAllClaims(VehicleState vehicle)
        {
            foreach (var claim in vehicle.Claims) claimsByNode[claim.Node].Remove(claim);
            vehicle.Claims.Clear();
        }

        // Whether a light tells this vehicle to stop, its front being this far from the line.
        private bool MustStopAt(StopLine line, float frontToLine, float speed)
        {
            if (frontToLine < -PastLineM) return false;
            var color = signalData.Timing.ColorAt(SignalClockS, line.OffsetS, line.Phase);
            if (color == SignalColor.Green) return false;
            // Too close to stop for a yellow: carry on through it
            return color == SignalColor.Red || speed * speed / (2 * YellowBrake) <= frontToLine;
        }

        private bool LightHoldsVehicle(VehicleState vehicle, DirectedRoad road, float roadStart)
        {
            if (signalData == null) return false;
            foreach (var line in road.Stops)
                if (MustStopAt(line, roadStart + line.At - vehicle.Length / 2, vehicle.Speed)) return true;
            return false;
        }

        // Lets the next junction know this vehicle is coming, unless a light is holding it first.
        private void AskForJunction(VehicleState vehicle)
        {
            var road = vehicle.Road;
            var lane = vehicle.Lane;
            var roadStart = -vehicle.Distance;
            var range = vehicle.Speed * vehicle.Speed / (2 * ComfortBrake) + vehicle.Speed * ReactionS + ClaimMarginM;
            var notice = Mathf.Max(range, vehicle.Speed * NoticeS);
            for (var i = 0; i < vehicle.Ahead.Count; i++)
            {
                if (LightHoldsVehicle(vehicle, road, roadStart)) return;
                var next = vehicle.Ahead[i];
                var nextLane = next.LaneFrom(lane);
                var toNode = roadStart + road.Length;
                if (toNode - vehicle.Length / 2 > notice) return;
                if (!vehicle.HasClaim(next))
                {
                    // It cannot go before the vehicle in front of it, so it has nothing to ask yet
                    if (i == 0 && vehicle.LaneIndex + 1 < road.Traffic[lane].Count && !road.Traffic[lane][vehicle.LaneIndex + 1].HasClaimFrom(road)) return;
                    Ask(vehicle, road, lane, next, nextLane, toNode, toNode - vehicle.Length / 2 <= range, i);
                    return;
                }
                roadStart = toNode;
                road = next;
                lane = nextLane;
            }
        }

        private void Ask(VehicleState vehicle, DirectedRoad road, int lane, DirectedRoad next, int nextLane, float toNode, bool canTake, int planIndex)
        {
            if (!requestsByNode.TryGetValue(road.ToNode, out var asked)) requestsByNode[road.ToNode] = asked = new List<NodeRequest>();
            if (asked.Count == 0) askedNodes.Add(road.ToNode);
            asked.Add(new NodeRequest
            {
                Vehicle = vehicle, From = road, FromLane = lane, To = next, ToLane = nextLane, ToNodeM = toNode, CanTake = canTake, PlanIndex = planIndex,
                Path = Movement.Through(road, lane, next, nextLane),
                Rank = ClassRank(road.Class) + (road.Stops.Count > 0 ? SignalRank : 0) + vehicle.WaitS / WaitPerRankS,
            });
        }

        private static readonly Comparison<NodeRequest> FirstToGo = (a, b) =>
        {
            var order = b.Rank.CompareTo(a.Rank);
            if (order == 0) order = a.ToNodeM.CompareTo(b.ToNodeM);
            return order != 0 ? order : string.CompareOrdinal(a.Vehicle.Id, b.Vehicle.Id);
        };

        // Each junction gives way in order of priority. Whoever it cannot let go yet, or who is still coming,
        // holds up anything lower that would cross their path, so a side road waits for a gap in the main road.
        private void AnswerJunctions(float dt)
        {
            foreach (var node in askedNodes)
            {
                var asked = requestsByNode[node];
                if (asked.Count > 1) asked.Sort(FirstToGo);
                denied.Clear();
                claimsByNode.TryGetValue(node, out var claims);
                foreach (var request in asked)
                {
                    var crossed = ConflictsWithClaims(request, claims) || ConflictsWithDenied(request);
                    if (!crossed && request.CanTake && !ShuttleHoldsNode(request.From) && HasRoomBeyond(request))
                    {
                        if (claims == null) claimsByNode[node] = claims = new List<Claim>();
                        var claim = new Claim { Vehicle = request.Vehicle, Node = node, From = request.From, To = request.To, FromLane = request.FromLane, ToLane = request.ToLane, Path = request.Path };
                        claims.Add(claim);
                        request.Vehicle.Claims.Add(claim);
                        request.Vehicle.WaitS = 0;
                        continue;
                    }
                    if (request.CanTake) request.Vehicle.WaitS += dt;
                    // Stopped by a full road, or by a shuttle, it is not about to go: others may use the gap
                    if (crossed || !request.CanTake) denied.Add(request);
                }
                asked.Clear();
            }
            askedNodes.Clear();
        }

        private static bool ConflictsWithClaims(NodeRequest request, List<Claim> claims)
        {
            if (claims == null) return false;
            foreach (var claim in claims)
                if (claim.Vehicle != request.Vehicle && Conflict(request.From, request.FromLane, request.To, request.ToLane, request.Path, claim.From, claim.FromLane, claim.To, claim.ToLane, claim.Path)) return true;
            return false;
        }

        private bool ConflictsWithDenied(NodeRequest request)
        {
            foreach (var other in denied)
                if (Conflict(request.From, request.FromLane, request.To, request.ToLane, request.Path, other.From, other.FromLane, other.To, other.ToLane, other.Path)) return true;
            return false;
        }

        private static bool Conflict(DirectedRoad fromA, int laneA, DirectedRoad toA, int exitA, in Movement a, DirectedRoad fromB, int laneB, DirectedRoad toB, int exitB, in Movement b)
        {
            // In the same lane one simply follows the other, whichever way each then turns
            if (fromA == fromB && laneA == laneB) return false;
            if (toA == toB && exitA == exitB) return true;
            return a.ComesWithin(b, ConflictM);
        }

        private static float SegmentDistance(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2)
        {
            float Side(Vector2 p, Vector2 q, Vector2 r) => (q.x - p.x) * (r.y - p.y) - (q.y - p.y) * (r.x - p.x);
            if (Side(a1, a2, b1) * Side(a1, a2, b2) < 0 && Side(b1, b2, a1) * Side(b1, b2, a2) < 0) return 0;
            return Mathf.Min(Mathf.Min(PointToSegment(a1, b1, b2), PointToSegment(a2, b1, b2)), Mathf.Min(PointToSegment(b1, a1, a2), PointToSegment(b2, a1, a2)));
        }

        private static float PointToSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            var along = b - a;
            var share = along.sqrMagnitude > 0 ? Mathf.Clamp01(Vector2.Dot(point - a, along) / along.sqrMagnitude) : 0;
            return Vector2.Distance(point, a + along * share);
        }

        // A shuttle does not give way: the backend moves it, so a junction it is about to use stays shut.
        private bool ShuttleHoldsNode(DirectedRoad arriving)
        {
            var node = arriving.Points[arriving.Points.Length - 1];
            foreach (var shuttle in shuttles)
            {
                var moving = shuttle.Speed > ShuttleMovingMps;
                var reach = moving ? ShuttleJunctionM : StandingShuttleJunctionM;
                var toNode = node - shuttle.Pose.Position;
                toNode.y = 0;
                if (toNode.sqrMagnitude > reach * reach) continue;
                if (!moving || Vector3.Dot(toNode, shuttle.Pose.Forward) > -shuttle.Pose.HalfLength - ClearedJunctionM) return true;
            }
            return false;
        }

        // Don't block the box: go only if there is room for the whole vehicle on the far side.
        private static bool HasRoomBeyond(NodeRequest request)
        {
            var vehicle = request.Vehicle;
            var needed = vehicle.Length + MinGapM;
            var passed = 0f;
            var road = request.To;
            var lane = request.ToLane;
            for (var i = request.PlanIndex; ;)
            {
                foreach (var span in road.Hazards)
                    if (passed + span.From < needed) return false;
                foreach (var other in road.Traffic[lane])
                {
                    if (other == vehicle) continue;
                    if (other.Speed < StandingMps && passed + other.Distance - other.Length / 2 < needed) return false;
                    break;
                }
                passed += road.Length;
                if (passed >= needed || ++i >= vehicle.Ahead.Count) return true;
                road = vehicle.Ahead[i];
                lane = road.LaneFrom(lane);
            }
        }

        private float SafeSpeed(VehicleState vehicle)
        {
            var limit = vehicle.Road.SpeedMps * vehicle.SpeedFactor;
            var half = vehicle.Length / 2;
            var road = vehicle.Road;
            var lane = vehicle.Lane;
            var roadStart = -vehicle.Distance;
            for (var i = 0; ; i++)
            {
                var traffic = road.Traffic[lane];
                var leaderIndex = i == 0 ? vehicle.LaneIndex + 1 : 0;
                var leader = leaderIndex < traffic.Count ? traffic[leaderIndex] : null;
                if (leader == vehicle) leader = leaderIndex + 1 < traffic.Count ? traffic[leaderIndex + 1] : null;
                if (leader != null) limit = Mathf.Min(limit, Krauss(vehicle.Speed, roadStart + leader.Distance - leader.Length / 2 - half - MinGapM, leader.Speed));

                foreach (var span in road.Hazards)
                    if (roadStart + span.From > 0) limit = Mathf.Min(limit, Krauss(vehicle.Speed, roadStart + span.From - half - StopMarginM, 0));
                if (signalData != null)
                    foreach (var line in road.Stops)
                        if (MustStopAt(line, roadStart + line.At - half, vehicle.Speed))
                            limit = Mathf.Min(limit, Krauss(vehicle.Speed, roadStart + line.At - half - StopMarginM, 0));

                // On past any leader, which may turn off and leave this vehicle facing what lies beyond
                if (i >= vehicle.Ahead.Count) break;
                var next = vehicle.Ahead[i];
                var toNode = roadStart + road.Length;
                if (!vehicle.HasClaim(next))
                {
                    limit = Mathf.Min(limit, Krauss(vehicle.Speed, toNode - half - NodeStopBackM, 0));
                    break;
                }
                // Slows for the turn in time to be at the turn's speed when it gets there
                var turn = TurnSpeed(vehicle, road, next);
                limit = Mathf.Min(limit, Mathf.Sqrt(turn * turn + 2 * ComfortBrake * Mathf.Max(0, toNode - half)));
                roadStart = toNode;
                road = next;
                lane = next.LaneFrom(lane);
                if (roadStart > PlanAheadM) break;
            }
            return ShuttleLimit(vehicle, limit);
        }

        // The fastest this vehicle can go and still stop behind its leader if the leader brakes.
        private static float Krauss(float speed, float gap, float leaderSpeed)
        {
            if (gap <= 0) return 0;
            return Mathf.Max(0, leaderSpeed + (gap - leaderSpeed * ReactionS) / ((speed + leaderSpeed) / (2 * ComfortBrake) + ReactionS));
        }

        private static float TurnSpeed(VehicleState vehicle, DirectedRoad road, DirectedRoad next)
        {
            var full = road.SpeedMps * vehicle.SpeedFactor;
            return Mathf.Lerp(3f, full, Mathf.InverseLerp(120f, 15f, Vector3.Angle(road.EndDirection, next.StartDirection)));
        }

        // A shuttle takes the middle of the road and cannot give way. Traffic follows one going its way,
        // stops for one crossing, and otherwise pulls aside for it to pass or to get past it.
        private float ShuttleLimit(VehicleState vehicle, float limit)
        {
            vehicle.TargetAside = 0;
            if (!vehicle.Placed) return limit;
            var half = vehicle.Length / 2;
            var left = Vector3.Cross(vehicle.Forward, Vector3.up).normalized;
            foreach (var shuttle in shuttles)
            {
                var to = shuttle.Pose.Position - vehicle.Position;
                to.y = 0;
                if (to.sqrMagnitude > ShuttleSightM * ShuttleSightM) continue;
                var ahead = Vector3.Dot(to, vehicle.Forward);
                var side = Vector3.Dot(to, left);
                var clearance = shuttle.Pose.HalfWidth + vehicle.HalfWidth + ShuttleClearanceM;
                var gap = ahead - shuttle.Pose.HalfLength - half - MinGapM;
                var alike = Vector3.Dot(shuttle.Pose.Forward, vehicle.Forward);
                var moving = shuttle.Speed > ShuttleMovingMps;

                if (Mathf.Abs(alike) < 0.5f)
                {
                    if (ahead > 0 && Mathf.Abs(side) < clearance + shuttle.Pose.HalfLength) limit = Mathf.Min(limit, Krauss(vehicle.Speed, gap, 0));
                    continue;
                }
                if (moving && alike > 0 && ahead > 0)
                {
                    if (Mathf.Abs(side) < clearance) limit = Mathf.Min(limit, Krauss(vehicle.Speed, gap, shuttle.Speed * alike));
                    continue;
                }
                // What is left is passing: one coming the other way, one standing ahead, or one catching up from behind
                if (ahead < -shuttle.Pose.HalfLength - half - ShuttlePassBehindM || ahead > ShuttlePassAheadM) continue;
                if (moving && alike > 0 && shuttle.Speed <= vehicle.Speed) continue;
                var laneSide = side + vehicle.Aside;
                var aside = laneSide <= 0 ? Mathf.Max(0, clearance + laneSide) : -Mathf.Max(0, clearance - laneSide);
                if (aside == 0) continue;
                if (Mathf.Abs(aside) > Mathf.Abs(vehicle.TargetAside)) vehicle.TargetAside = Mathf.Clamp(aside, -MaxAsideM, MaxAsideM);
                if (ahead > 0) limit = Mathf.Min(limit, PassingMps);
                // Not yet out of its way: wait where it can still get there
                if (ahead > 0 && Mathf.Abs(side) < clearance - ShuttleBesideM) limit = Mathf.Min(limit, Krauss(vehicle.Speed, gap, 0));
            }
            return limit;
        }

        private void Move(VehicleState vehicle, float dt)
        {
            var rate = vehicle.TargetSpeed < vehicle.Speed ? HardBrake : Acceleration(vehicle.Model);
            vehicle.Speed = Mathf.MoveTowards(vehicle.Speed, vehicle.TargetSpeed, rate * dt);
            vehicle.Aside = Mathf.MoveTowards(vehicle.Aside, vehicle.TargetAside, AsideMps * dt);
            vehicle.StoppedS = vehicle.Speed < StoppedMps ? vehicle.StoppedS + dt : 0;
            if (vehicle.StoppedS > StuckS) { EnterFromOutside(vehicle); return; }

            var before = vehicle.Distance;
            vehicle.Distance += vehicle.Speed * dt;
            NoteRedLightRun(vehicle, before);

            var safety = 0;
            while (vehicle.Distance > vehicle.Road.Length && safety++ < 6)
            {
                if (vehicle.Ahead.Count == 0) { EnterFromOutside(vehicle); return; }
                vehicle.Distance -= vehicle.Road.Length;
                var next = vehicle.Ahead[0];
                vehicle.Ahead.RemoveAt(0);
                foreach (var claim in vehicle.Claims)
                    if (claim.To == next) claim.Entered = true;
                PutOn(vehicle, next);
            }
        }

        private void NoteRedLightRun(VehicleState vehicle, float before)
        {
            if (signalData == null) return;
            foreach (var line in vehicle.Road.Stops)
                if (before + vehicle.Length / 2 <= line.At && vehicle.Distance + vehicle.Length / 2 > line.At
                    && signalData.Timing.ColorAt(SignalClockS, line.OffsetS, line.Phase) == SignalColor.Red) redLightRuns++;
        }

        // A road with no way on leaves the area, so the vehicle has gone: another comes in at the edge.
        // Otherwise those roads would collect the traffic until the rest of the city ran empty. With no
        // room on any road in, it waits outside for one.
        private void EnterFromOutside(VehicleState vehicle)
        {
            ReleaseAllClaims(vehicle);
            vehicle.Ahead.Clear();
            vehicle.StoppedS = vehicle.WaitS = vehicle.Aside = 0;
            vehicle.Placed = false;
            vehicle.Outside = true;
            var entries = IsLarge(vehicle.Model) ? largeEntryRoads : entryRoads;
            if (entries.Count == 0) entries = entryRoads;
            if (entries.Count == 0) return;
            vehicle.Rng = Step(vehicle.Rng);
            for (var i = 0; i < Mathf.Min(entries.Count, 8); i++)
            {
                var road = entries[(int)((vehicle.Rng + (uint)i) % (uint)entries.Count)];
                if (road.Hazards.Count > 0 || !HasRoomAtStart(road, road.LaneFrom(vehicle.Lane), vehicle.Length)) continue;
                vehicle.Outside = false;
                vehicle.Distance = 0;
                vehicle.Speed = road.Stops.Count > 0 ? 0 : Mathf.Min(vehicle.Speed, EnterMps);
                PutOn(vehicle, road);
                road.Traffic[vehicle.Lane].Insert(0, vehicle);
                if (!road.Filled) { road.Filled = true; filledRoads.Add(road); }
                return;
            }
        }

        private bool HasRoomAtStart(DirectedRoad road, int lane, float length)
        {
            // A road in may still have its own other direction turning back onto it
            if (claimsByNode.TryGetValue(road.FromNode, out var claims))
                foreach (var claim in claims)
                    if (claim.To == road) return false;
            foreach (var other in road.Traffic[lane])
                if (other.Road == road && other.Distance - other.Length / 2 < length + MinGapM) return false;
            return true;
        }

        private void PutOn(VehicleState vehicle, DirectedRoad road)
        {
            vehicle.Road = road;
            vehicle.Lane = road.LaneFrom(vehicle.Lane);
        }

        private static float ClassRank(string roadClass) =>
            roadClass == "primary" ? 6f :
            roadClass == "primary_link" ? 5f :
            roadClass == "secondary" ? 4f :
            roadClass == "secondary_link" || roadClass == "tertiary" ? 3f :
            roadClass == "tertiary_link" ? 2f :
            roadClass == "service" ? 0f : 1f;

        private struct StopLine
        {
            public float At;
            public int OffsetS, Phase;
        }

        private struct Span
        {
            public float From, To;
        }

        private sealed class ShuttleState
        {
            public TrafficObstacle Pose;
            public Vector3 Last;
            public float Speed;
        }

        // The way a vehicle drives through a junction: along its lane to the junction, across it, and out
        // along its next lane. Its place on the road goes through these points, whatever is drawn.
        private readonly struct Movement
        {
            private readonly Vector2 inLane, arrive, leave, outLane;

            private Movement(Vector2 inLane, Vector2 arrive, Vector2 leave, Vector2 outLane)
            {
                this.inLane = inLane; this.arrive = arrive; this.leave = leave; this.outLane = outLane;
            }

            public static Movement Through(DirectedRoad road, int lane, DirectedRoad next, int nextLane)
            {
                var offset = road.LaneOffset(lane);
                var nextOffset = next.LaneOffset(nextLane);
                return new Movement(Flat(road, road.Length - MovementReachM, offset), Flat(road, road.Length, offset), Flat(next, 0, nextOffset), Flat(next, MovementReachM, nextOffset));
            }

            private static Vector2 Flat(DirectedRoad road, float along, float offset)
            {
                road.Sample(along, offset, out var at, out _);
                return new Vector2(at.x, at.z);
            }

            public bool ComesWithin(in Movement other, float distance) =>
                other.StretchComesWithin(inLane, arrive, distance) || other.StretchComesWithin(arrive, leave, distance) || other.StretchComesWithin(leave, outLane, distance);

            private bool StretchComesWithin(Vector2 a, Vector2 b, float distance) =>
                SegmentDistance(a, b, inLane, arrive) < distance || SegmentDistance(a, b, arrive, leave) < distance || SegmentDistance(a, b, leave, outLane) < distance;
        }

        // A vehicle's right to go through a junction, from when the junction gives way until it has left.
        private sealed class Claim
        {
            public VehicleState Vehicle;
            public long Node;
            public DirectedRoad From, To;
            public int FromLane, ToLane;
            public Movement Path;
            public bool Entered;
        }

        private struct NodeRequest
        {
            public VehicleState Vehicle;
            public DirectedRoad From, To;
            public int FromLane, ToLane, PlanIndex;
            public Movement Path;
            public float ToNodeM, Rank;
            public bool CanTake;
        }
    }
}
