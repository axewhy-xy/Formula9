using System;
using System.Collections.Generic;
using Formula9.Backend;
using UnityEngine;

namespace Formula9.World
{
    // How the traffic drives the shuttles. The backend says how far along its route each shuttle has
    // got, and the shuttle drawn here never goes past that. Short of it, the shuttle drives as the rest
    // of the traffic does: in the kerb lane, slowing for a bend, stopping at a red light, and following
    // or giving way to any vehicle in its way. So it can fall behind the backend, and it catches up, no
    // faster than the road allows, once the way is clear. Off its route, or too far behind to catch up,
    // it is put where the backend says. This is for a session whose backend sends no traffic frames: one
    // that drives the traffic itself sends its shuttles' bodies with it, and they are drawn there
    // (CityTrafficFollow), so they never fall behind.
    public sealed partial class CityTrafficRenderer
    {
        // A backend pose this close to a leg of the route, facing along it, is on the route
        private const float ShuttleOnRouteM = 8f, ShuttleFacing = 0.3f;
        // Further behind the backend than this, or further ahead than the slack, the shuttle is put there
        private const float ShuttleMaxLagM = 400f, ShuttleAheadSlackM = 5f;
        // It closes on the backend's pose as on a leader, though reacting quicker than to a stranger
        private const float ShuttleTargetReactionS = 0.4f;
        private const float ShuttleAccel = 1.5f, ShuttleLaneMps = 1f, ShuttleTurnRadiusM = 9f;
        // With no road matched it may catch up this much faster than the backend drives
        private const float ShuttleCatchUp = 1.3f;
        // How far ahead it looks for vehicles in its way, and in steps of how much road
        private const float ShuttleLookM = 60f, ShuttleLookStepM = 2f;
        // The room it leaves beside a vehicle; one coming the other way need only not touch it
        private const float ShuttleSideRoomM = 0.3f, ShuttleOncomingRoomM = -0.1f;
        // A stop line this close to the route, on a leg running its way, is one the shuttle stops at
        private const float ShuttleStopMatchM = 8f, ShuttleStopAlign = 0.7f, ShuttleStopApartM = 10f;
        // A road this close to the route, running its way, is the one the shuttle drives on
        private const float ShuttleRoadAlign = 0.7f;
        // A bend is measured over this much route either side of a corner, and only one this sharp slows it
        private const float ShuttleBendSpanM = 8f, ShuttleBendDeg = 15f;
        // The road either side of where it is that the shuttle points along, so it steers round a corner
        private const float ShuttleFacingSpanM = 4f;
        // Kept waiting at a junction by traffic this long, it takes its turn; this long, and whatever it
        // waits for is stuck, so it leaves the area as a jammed vehicle does
        private const float ShuttleTakeTurnS = 4f, ShuttleGiveUpS = 30f;
        // A shuttle watches one coming across its way this far ahead of it, or this long ahead at its speed
        private const float ShuttleCrossNoticeM = 40f, ShuttleCrossNoticeS = 4f;
        // Bodies closer than this are only touching, as two lanes' vehicles side by side may
        private const float TouchM = 0.1f;

        private readonly Dictionary<string, ShuttleRoute> shuttleRoutes = new Dictionary<string, ShuttleRoute>();
        private readonly List<Vector2> lookPoints = new List<Vector2>();
        private int shuttleRedLightRuns;

        // Hands a shuttle to the traffic to drive along its route, towards the backend's pose. False when
        // that pose is not on the route, so the caller places the shuttle itself and lists it in SetShuttles.
        public bool DriveShuttle(string id, string routeId, IReadOnlyList<Vector2> route, Vector3 position, Quaternion rotation, float backendMps, float halfLength, float halfWidth)
        {
            var shuttle = shuttles.Find(s => s.Pose.Id == id);
            var path = route != null && route.Count >= 2 ? RouteFor(routeId ?? "", route) : null;
            var at = new Vector2(position.x, position.z);
            var facing = rotation * Vector3.forward;
            var heading = new Vector2(facing.x, facing.z).normalized;
            var target = -1f;
            if (path != null && shuttle?.Route != null)
            {
                if (shuttle.Route != path)
                {
                    // A new route, as for a detour: it carries on from where it is drawn, if that is on the new one
                    var along = Locate(path, RoutePoint(shuttle.Route, shuttle.Along), Flat(shuttle.Pose.Forward), 0, path.Length);
                    if (along >= 0) { shuttle.Route = path; shuttle.Along = along; }
                }
                if (shuttle.Route == path)
                {
                    target = Locate(path, at, heading, shuttle.Along - ShuttleAheadSlackM, shuttle.Along + ShuttleMaxLagM);
                    // On a loop the backend may already be on its next lap
                    if (target < 0 && path.Closed)
                    {
                        target = Locate(path, at, heading, 0, shuttle.Along + ShuttleMaxLagM - path.Length);
                        if (target >= 0) target += path.Length;
                    }
                }
            }
            var snap = target < 0;
            if (snap && path != null) target = Locate(path, at, heading, 0, path.Length);
            if (target < 0)
            {
                // Not on a route it can follow: the caller poses it, and the traffic keeps clear of that
                if (shuttle != null && shuttle.Route != null) { shuttle.Route = null; shuttle.Last = shuttle.Pose.Position; shuttle.GivesWayTo.Clear(); }
                return false;
            }

            if (shuttle == null) shuttles.Add(shuttle = new ShuttleState());
            shuttle.Pose.Id = id;
            shuttle.Pose.HalfLength = halfLength;
            shuttle.Pose.HalfWidth = halfWidth;
            shuttle.Height = position.y;
            shuttle.BackendMps = Mathf.Max(0, backendMps);
            shuttle.TopBackendMps = Mathf.Max(shuttle.TopBackendMps, shuttle.BackendMps);
            shuttle.TargetAlong = target;
            if (snap || shuttle.Route == null) shuttle.Placed = false;
            shuttle.Route = path;
            if (!shuttle.Placed)
            {
                shuttle.Along = target;
                shuttle.Speed = shuttle.BackendMps;
                shuttle.WaitS = 0;
                shuttle.GivesWayTo.Clear();
                RoadUnder(RoutePoint(path, target), RouteDirection(path, target), out shuttle.TargetAside);
                shuttle.Aside = shuttle.TargetAside;
                PlaceShuttle(shuttle, 0);
            }
            return true;
        }

        // The lights run on the session's clock, the backend's simulation time, which its shuttles stop by.
        // Between snapshots the traffic keeps time itself; drifted further than this, as when long frames
        // were cut short, it is set right.
        private const double SignalClockSlackS = 0.5;

        public void FollowSignalClock(double simulationTimeS)
        {
            if (Math.Abs(SignalClockS - simulationTimeS) > SignalClockSlackS) SignalClockS = simulationTimeS;
        }

        public bool DrivesShuttle(string id) => shuttles.Exists(s => s.Route != null && s.Pose.Id == id);

        // Where a shuttle the traffic drives is drawn.
        public bool TryGetShuttlePose(string id, out Vector3 position, out Quaternion rotation)
        {
            var shuttle = shuttles.Find(s => s.Route != null && s.Pose.Id == id);
            position = shuttle?.Pose.Position ?? default;
            rotation = shuttle != null ? Quaternion.LookRotation(shuttle.Pose.Forward, Vector3.up) : Quaternion.identity;
            return shuttle != null;
        }

        public void ForgetShuttle(string id) => shuttles.RemoveAll(s => s.Pose.Id == id);

        // Every shuttle is put straight at its next pose, as for a new session or a view that was not being
        // drawn; following the backend, so is every vehicle, at the time the frames are drawn from.
        public void ResetShuttles()
        {
            shuttles.Clear();
            shuttleRoutes.Clear();
            if (!FollowsBackend) return;
            followS = double.NaN;
            foreach (var vehicle in vehicles) vehicle.Placed = false;
        }

        private ShuttleRoute RouteFor(string id, IReadOnlyList<Vector2> points)
        {
            if (shuttleRoutes.TryGetValue(id, out var known) && known.Matches(points)) return known;
            var route = ShuttleRoute.Create(id, points);
            if (route == null) return null;
            FindStopLines(route);
            shuttleRoutes[id] = route;
            return route;
        }

        // The lights' stop lines across the route, where it runs the way the light faces.
        private void FindStopLines(ShuttleRoute route)
        {
            if (signalData == null) return;
            foreach (var junction in signalData.Junctions)
                foreach (var approach in junction.Approaches)
                {
                    var heading = approach.HeadingDeg * Mathf.Deg2Rad;
                    var travel = new Vector2(Mathf.Cos(heading), Mathf.Sin(heading));
                    var stop = new Vector2(approach.StopX, approach.StopY);
                    for (var i = 0; i + 1 < route.Points.Length; i++)
                    {
                        var start = route.Points[i];
                        var run = route.Points[i + 1] - start;
                        var length = run.magnitude;
                        var share = Mathf.Clamp01(Vector2.Dot(stop - start, run) / (length * length));
                        if (Vector2.Distance(stop, start + run * share) > ShuttleStopMatchM || Vector2.Dot(run / length, travel) < ShuttleStopAlign) continue;
                        var at = route.Along[i] + length * share;
                        if (route.Stops.Exists(s => Mathf.Abs(s.At - at) < ShuttleStopApartM)) continue;
                        route.Stops.Add(new StopLine { At = at, OffsetS = junction.OffsetS, Phase = approach.Phase });
                    }
                }
        }

        // Before the junctions are asked: a shuttle kept waiting so long that what it waits for must be
        // stuck has that traffic leave the area, as a vehicle stuck in a jam does.
        private void ClearShuttleJams()
        {
            foreach (var shuttle in shuttles)
            {
                if (shuttle.Route == null || shuttle.WaitS < ShuttleGiveUpS) continue;
                foreach (var vehicle in shuttle.GivesWayTo)
                    if (!vehicle.Outside && vehicle.Speed < StoppedMps) EnterFromOutside(vehicle);
                shuttle.GivesWayTo.Clear();
                shuttle.WaitS = 0;
            }
        }

        // The speed a shuttle may go from what is around it now, as SafeSpeed is for the traffic.
        private void PlanShuttle(ShuttleState shuttle, float dt)
        {
            var route = shuttle.Route;
            var front = shuttle.Pose.HalfLength;
            var road = RoadUnder(RoutePoint(route, shuttle.Along), RouteDirection(route, shuttle.Along), out shuttle.TargetAside);
            var cruise = Mathf.Max(shuttle.BackendMps, road != null ? road.SpeedMps : shuttle.TopBackendMps * ShuttleCatchUp);

            // Never past where the backend says it is
            var toTarget = shuttle.TargetAlong - shuttle.Along;
            var limit = Mathf.Min(cruise, Krauss(shuttle.Speed, toTarget, shuttle.BackendMps, ShuttleTargetReactionS));
            var stopsWithin = Mathf.Max(0, toTarget);
            foreach (var bend in route.Bends)
            {
                var toBend = Ahead(route, bend.At, shuttle.Along);
                if (toBend < 0 || toBend > PlanAheadM) continue;
                var turn = Mathf.Lerp(3f, cruise, Mathf.InverseLerp(120f, 15f, bend.Degrees));
                limit = Mathf.Min(limit, Mathf.Sqrt(turn * turn + 2 * ComfortBrake * toBend));
            }

            var rules = float.MaxValue;
            var heldByLight = false;
            if (signalData != null)
                foreach (var line in route.Stops)
                {
                    var frontToLine = Ahead(route, line.At, shuttle.Along) - front;
                    if (frontToLine < -PastLineM || frontToLine > PlanAheadM || !MustStopAt(line, frontToLine, shuttle.Speed)) continue;
                    rules = Mathf.Min(rules, Krauss(shuttle.Speed, frontToLine - StopMarginM, 0));
                    stopsWithin = Mathf.Min(stopsWithin, Mathf.Max(0, frontToLine - StopMarginM));
                    heldByLight = true;
                }
            var heldByTraffic = GiveWay(shuttle, cruise, ref rules, ref stopsWithin);
            if (SomethingAlongside(shuttle, shuttle.TargetAside - shuttle.Aside)) shuttle.TargetAside = shuttle.Aside;

            shuttle.TargetSpeed = Mathf.Min(limit, rules);
            // Held up by a light or the traffic, not just waiting on the backend, as at one of its stops
            shuttle.InTraffic = rules <= limit && rules < cruise;
            shuttle.StopsWithinM = stopsWithin;
            shuttle.WaitS = heldByTraffic && !heldByLight && shuttle.Speed < StoppedMps ? shuttle.WaitS + dt : 0;
        }

        // Follows a vehicle in front going its way and stops for one standing or crossing in its path,
        // which it then gives way to. True when one slows it down.
        private bool GiveWay(ShuttleState shuttle, float cruise, ref float rules, ref float stopsWithin)
        {
            shuttle.GivesWayTo.Clear();
            var route = shuttle.Route;
            var look = Mathf.Max(ShuttleLookM, shuttle.Speed * shuttle.Speed / (2 * ComfortBrake) + shuttle.Speed * ReactionS + ClaimMarginM);
            if (!route.Closed) look = Mathf.Min(look, route.Length - shuttle.Along);
            // The path its body sweeps, through its own lane
            lookPoints.Clear();
            for (var d = 0f; ; d = Mathf.Min(d + ShuttleLookStepM, look))
            {
                var heading = RouteDirection(route, shuttle.Along + d);
                lookPoints.Add(RoutePoint(route, shuttle.Along + d) + new Vector2(-heading.y, heading.x) * shuttle.Aside);
                if (d >= look) break;
            }
            if (lookPoints.Count < 2) return false;

            var held = false;
            var origin = Flat(shuttle.Pose.Position);
            var reach = look + ShuttleLookStepM + shuttle.Pose.HalfLength;
            foreach (var vehicle in vehicles)
            {
                if (vehicle.Outside || !vehicle.Placed) continue;
                var at = Flat(vehicle.Position);
                if ((at - origin).sqrMagnitude > reach * reach) continue;
                if (!AlongLook(at, out var along, out var side, out var tangent)) continue;
                var facing = Flat(vehicle.Forward).normalized;
                var alike = Vector2.Dot(facing, tangent);
                // One given way at a junction, coming across, is watched for where it is going, not only where it is
                if (Mathf.Abs(alike) <= 0.5f && vehicle.Claims.Count > 0 && vehicle.Speed > StandingMps
                    && (along <= 0 || side >= shuttle.Pose.HalfWidth + vehicle.Length / 2 + ShuttleSideRoomM)
                    && CrossesLook(shuttle, ref at, facing, vehicle.Speed, vehicle.Length / 2, vehicle.HalfWidth, out along, out side, out tangent))
                    alike = Vector2.Dot(facing, tangent);
                if (along <= 0) continue;
                var across = Mathf.Abs(facing.x * tangent.y - facing.y * tangent.x);
                var sideReach = across * vehicle.Length / 2 + Mathf.Abs(alike) * vehicle.HalfWidth;
                var room = alike < -0.5f ? ShuttleOncomingRoomM : ShuttleSideRoomM;
                if (side >= shuttle.Pose.HalfWidth + sideReach + room) continue;
                var gap = along - Mathf.Abs(alike) * vehicle.Length / 2 - across * vehicle.HalfWidth - shuttle.Pose.HalfLength - MinGapM;
                var leaderMps = Mathf.Max(0, vehicle.Speed * alike);
                var limit = Krauss(shuttle.Speed, gap, leaderMps);
                if (limit < cruise) held = true;
                rules = Mathf.Min(rules, limit);
                if (leaderMps >= StandingMps) continue;
                // It waits for this one, which may go first even where it would otherwise wait for the shuttle
                shuttle.GivesWayTo.Add(vehicle);
                stopsWithin = Mathf.Min(stopsWithin, Mathf.Max(0, gap));
            }
            foreach (var other in shuttles)
            {
                if (other == shuttle) continue;
                var facing = Flat(other.Pose.Forward).normalized;
                var at = Flat(other.Pose.Position);
                if ((at - origin).sqrMagnitude > (reach + ShuttleCrossNoticeM) * (reach + ShuttleCrossNoticeM)) continue;
                if (!AlongLook(at, out var along, out var side, out var tangent)) continue;
                var alike = Vector2.Dot(facing, tangent);
                if (alike <= 0.5f && other.Route != null)
                {
                    // Of two shuttles the traffic drives across each other's way, the one later by name waits,
                    // watching where the other is going; the first waits only for one moving across it now
                    var first = string.CompareOrdinal(shuttle.Pose.Id, other.Pose.Id) < 0;
                    if (first && (other.Speed < ShuttleMovingMps || along <= 0 || side >= shuttle.Pose.HalfWidth + other.Pose.HalfLength + ShuttleSideRoomM)) continue;
                    if (!first && !CrossesLook(shuttle, ref at, facing, other.Speed, other.Pose.HalfLength, other.Pose.HalfWidth, out along, out side, out tangent)) continue;
                    alike = Vector2.Dot(facing, tangent);
                }
                if (along <= 0) continue;
                var across = Mathf.Abs(facing.x * tangent.y - facing.y * tangent.x);
                var sideReach = across * other.Pose.HalfLength + Mathf.Abs(alike) * other.Pose.HalfWidth;
                var room = alike < -0.5f ? ShuttleOncomingRoomM : ShuttleSideRoomM;
                if (side >= shuttle.Pose.HalfWidth + sideReach + room) continue;
                var gap = along - Mathf.Abs(alike) * other.Pose.HalfLength - across * other.Pose.HalfWidth - shuttle.Pose.HalfLength - MinGapM;
                var leaderMps = Mathf.Max(0, other.Speed * alike);
                var limit = Krauss(shuttle.Speed, gap, leaderMps);
                if (limit < cruise) held = true;
                rules = Mathf.Min(rules, limit);
                if (leaderMps < StandingMps) stopsWithin = Mathf.Min(stopsWithin, Mathf.Max(0, gap));
            }
            return held;
        }

        // The first place, from where a vehicle is now to where it will be a little later, that is in
        // this shuttle's path ahead, as a driver watches one coming across. At is moved there.
        private bool CrossesLook(ShuttleState shuttle, ref Vector2 at, Vector2 facing, float speed, float halfLength, float halfWidth, out float along, out float side, out Vector2 tangent)
        {
            var start = at;
            var notice = Mathf.Min(ShuttleCrossNoticeM, speed * ShuttleCrossNoticeS) + halfLength;
            for (var d = 0f; ; d = Mathf.Min(d + ShuttleLookStepM, notice))
            {
                if (AlongLook(start + facing * d, out along, out side, out tangent) && along > 0
                    && side < shuttle.Pose.HalfWidth + halfWidth + ShuttleSideRoomM)
                {
                    at = start + facing * d;
                    return true;
                }
                if (d >= notice)
                {
                    AlongLook(start, out along, out side, out tangent);
                    return false;
                }
            }
        }

        // Whether a vehicle beside the shuttle is in the way of it moving across, to the left for a
        // positive shift, as a driver looks before changing lane.
        private bool SomethingAlongside(ShuttleState shuttle, float shift)
        {
            if (Mathf.Abs(shift) < 0.01f) return false;
            var forward = Flat(shuttle.Pose.Forward).normalized;
            var left = new Vector2(-forward.y, forward.x);
            var origin = Flat(shuttle.Pose.Position);
            var reach = shuttle.Pose.HalfLength * 2 + MinGapM;
            foreach (var vehicle in vehicles)
            {
                if (vehicle.Outside || !vehicle.Placed) continue;
                var to = Flat(vehicle.Position) - origin;
                if (to.sqrMagnitude > reach * reach) continue;
                var side = Vector2.Dot(to, left);
                if (side * shift <= 0) continue;
                var facing = Flat(vehicle.Forward).normalized;
                var across = Mathf.Abs(Vector2.Dot(facing, left));
                var along = Mathf.Abs(Vector2.Dot(facing, forward));
                // Its body overlaps the shuttle's length, and is within the room the shuttle keeps beside it
                if (Mathf.Abs(Vector2.Dot(to, forward)) > shuttle.Pose.HalfLength + along * vehicle.Length / 2 + across * vehicle.HalfWidth + MinGapM) continue;
                if (Mathf.Abs(side) < shuttle.Pose.HalfWidth + across * vehicle.Length / 2 + along * vehicle.HalfWidth + ShuttleSideRoomM + ShuttleLaneMps) return true;
            }
            return false;
        }

        // Where a point is beside the shuttle's path ahead: how far along it, how far to the side, and which way it runs there.
        private bool AlongLook(Vector2 point, out float along, out float side, out Vector2 tangent)
        {
            along = 0; side = float.MaxValue; tangent = Vector2.zero;
            var travelled = 0f;
            for (var i = 0; i + 1 < lookPoints.Count; i++)
            {
                var start = lookPoints[i];
                var run = lookPoints[i + 1] - start;
                var length = run.magnitude;
                if (length <= 0) continue;
                var share = Mathf.Clamp01(Vector2.Dot(point - start, run) / (length * length));
                var distance = Vector2.Distance(point, start + run * share);
                if (distance < side) { side = distance; along = travelled + length * share; tangent = run / length; }
                travelled += length;
            }
            return side < float.MaxValue;
        }

        private void MoveShuttle(ShuttleState shuttle, float dt)
        {
            var route = shuttle.Route;
            var rate = shuttle.TargetSpeed < shuttle.Speed ? HardBrake : ShuttleAccel;
            shuttle.Speed = Mathf.MoveTowards(shuttle.Speed, shuttle.TargetSpeed, rate * dt);
            var before = shuttle.Along;
            shuttle.Along = Mathf.Min(shuttle.Along + shuttle.Speed * dt, Mathf.Max(shuttle.Along, shuttle.TargetAlong));
            if (!route.Closed) shuttle.Along = Mathf.Min(shuttle.Along, route.Length);
            // Held at the backend's pose, it goes no faster than it moves
            shuttle.Speed = Mathf.Min(shuttle.Speed, (shuttle.Along - before) / dt);
            NoteShuttleRedLightRun(shuttle, before);
            if (route.Closed && shuttle.Along >= route.Length)
            {
                shuttle.Along -= route.Length;
                shuttle.TargetAlong -= route.Length;
            }
            shuttle.Aside = Mathf.MoveTowards(shuttle.Aside, shuttle.TargetAside, ShuttleLaneMps * dt);
            PlaceShuttle(shuttle, dt);
        }

        private void NoteShuttleRedLightRun(ShuttleState shuttle, float before)
        {
            if (signalData == null) return;
            var front = shuttle.Pose.HalfLength;
            foreach (var line in shuttle.Route.Stops)
                if (before + front <= line.At && shuttle.Along + front > line.At
                    && signalData.Timing.ColorAt(SignalClockS, line.OffsetS, line.Phase) == SignalColor.Red) shuttleRedLightRuns++;
        }

        private static void PlaceShuttle(ShuttleState shuttle, float dt)
        {
            var heading = RouteDirection(shuttle.Route, shuttle.Along);
            var at = RoutePoint(shuttle.Route, shuttle.Along) + new Vector2(-heading.y, heading.x) * shuttle.Aside;
            shuttle.Pose.Position = new Vector3(at.x, shuttle.Height, at.y);
            var forward = new Vector3(heading.x, 0, heading.y);
            var turnRate = Mathf.Max(shuttle.Speed / ShuttleTurnRadiusM, MinTurnRateRad);
            shuttle.Pose.Forward = shuttle.Placed && dt > 0 ? Vector3.RotateTowards(shuttle.Pose.Forward, forward, turnRate * dt, 0) : forward;
            shuttle.Placed = true;
        }

        // The road the route runs along here, and how far to the left of the route its kerb lane is.
        // The route need not follow the road's centreline exactly, so the lane is measured from the route.
        private DirectedRoad RoadUnder(Vector2 at, Vector2 heading, out float aside)
        {
            aside = 0;
            if (roads.Count == 0) return null;
            if (roadGrid.Count == 0) BuildRoadGrid();
            if (!roadGrid.TryGetValue(GridCell(at), out var near)) return null;
            DirectedRoad best = null;
            var bestAlong = 0f;
            var bestDistance = HazardMatchM;
            foreach (var road in near)
            {
                var distance = road.Project(at, out var along, out var tangent);
                if (distance > bestDistance || Vector2.Dot(Flat(tangent).normalized, heading) < ShuttleRoadAlign) continue;
                best = road; bestAlong = along; bestDistance = distance;
            }
            if (best == null) return null;
            best.Sample(bestAlong, best.LaneOffset(best.Lanes - 1), out var lane, out _);
            aside = Vector2.Dot(Flat(lane) - at, new Vector2(-heading.y, heading.x));
            return best;
        }

        // How far ahead of the shuttle a place on its route is, the next time round on a loop.
        private static float Ahead(ShuttleRoute route, float at, float along)
        {
            var ahead = at - along;
            if (route.Closed && ahead < -route.Length / 2) ahead += route.Length;
            return ahead;
        }

        // The nearest place on the route to a point, on a leg facing its way, between two distances along; -1 for none.
        private static float Locate(ShuttleRoute route, Vector2 point, Vector2 heading, float from, float to)
        {
            var best = -1f;
            var bestDistance = ShuttleOnRouteM;
            for (var i = 0; i + 1 < route.Points.Length; i++)
            {
                if (route.Along[i + 1] < from || route.Along[i] > to) continue;
                var start = route.Points[i];
                var run = route.Points[i + 1] - start;
                var length = run.magnitude;
                if (heading.sqrMagnitude > 0 && Vector2.Dot(run / length, heading) < ShuttleFacing) continue;
                var share = Mathf.Clamp01(Vector2.Dot(point - start, run) / (length * length));
                var distance = Vector2.Distance(point, start + run * share);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = Mathf.Clamp(route.Along[i] + length * share, from, to);
            }
            return best;
        }

        private static Vector2 RoutePoint(ShuttleRoute route, float along)
        {
            along = route.Closed ? Mathf.Repeat(along, route.Length) : Mathf.Clamp(along, 0, route.Length);
            var index = Array.BinarySearch(route.Along, along);
            if (index >= 0) return route.Points[index];
            index = Mathf.Clamp(~index, 1, route.Points.Length - 1);
            var start = route.Along[index - 1];
            return Vector2.Lerp(route.Points[index - 1], route.Points[index], (along - start) / (route.Along[index] - start));
        }

        // The way the route runs here, looking a little either side so a corner turns smoothly.
        private static Vector2 RouteDirection(ShuttleRoute route, float along)
        {
            var behind = route.Closed ? along - ShuttleFacingSpanM : Mathf.Max(0, along - ShuttleFacingSpanM);
            var ahead = route.Closed ? along + ShuttleFacingSpanM : Mathf.Min(route.Length, along + ShuttleFacingSpanM);
            var direction = RoutePoint(route, ahead) - RoutePoint(route, behind);
            return direction.sqrMagnitude > 1e-6f ? direction.normalized : (route.Points[1] - route.Points[0]).normalized;
        }

        private static Vector2 Flat(Vector3 point) => new Vector2(point.x, point.z);

        private static float Krauss(float speed, float gap, float leaderSpeed, float reaction)
        {
            if (gap <= 0) return 0;
            return Mathf.Max(0, leaderSpeed + (gap - leaderSpeed * reaction) / ((speed + leaderSpeed) / (2 * ComfortBrake) + reaction));
        }

        // Whether two vehicles' bodies, as rectangles seen from above, overlap by more than a touch.
        private static bool Overlap(Vector2 a, Vector2 aForward, float aHalfLength, float aHalfWidth, Vector2 b, Vector2 bForward, float bHalfLength, float bHalfWidth)
        {
            var aLeft = new Vector2(-aForward.y, aForward.x);
            var bLeft = new Vector2(-bForward.y, bForward.x);
            foreach (var axis in new[] { aForward, aLeft, bForward, bLeft })
            {
                var aReach = aHalfLength * Mathf.Abs(Vector2.Dot(aForward, axis)) + aHalfWidth * Mathf.Abs(Vector2.Dot(aLeft, axis));
                var bReach = bHalfLength * Mathf.Abs(Vector2.Dot(bForward, axis)) + bHalfWidth * Mathf.Abs(Vector2.Dot(bLeft, axis));
                if (Mathf.Abs(Vector2.Dot(b - a, axis)) > aReach + bReach - TouchM) return false;
            }
            return true;
        }

        // Vehicles touching a shuttle, and pairs of shuttles touching each other.
        private int ShuttleOverlaps()
        {
            var count = 0;
            for (var i = 0; i < shuttles.Count; i++)
            {
                var shuttle = shuttles[i];
                var forward = Flat(shuttle.Pose.Forward).normalized;
                for (var j = i + 1; j < shuttles.Count; j++)
                    if (Overlap(Flat(shuttle.Pose.Position), forward, shuttle.Pose.HalfLength, shuttle.Pose.HalfWidth,
                            Flat(shuttles[j].Pose.Position), Flat(shuttles[j].Pose.Forward).normalized, shuttles[j].Pose.HalfLength, shuttles[j].Pose.HalfWidth)) count++;
                foreach (var vehicle in vehicles)
                    if (!vehicle.Outside && vehicle.Placed && Overlap(Flat(shuttle.Pose.Position), forward, shuttle.Pose.HalfLength, shuttle.Pose.HalfWidth,
                            Flat(vehicle.Position), Flat(vehicle.Forward).normalized, vehicle.Length / 2, vehicle.HalfWidth)) count++;
            }
            return count;
        }

        // A route as the shuttle follows it: its points, how far along each is, its stop lines and its bends.
        private sealed class ShuttleRoute
        {
            public string Id;
            public Vector2[] Source, Points;
            public float[] Along;
            public float Length;
            // Ends where it starts, so the shuttle goes round again
            public bool Closed;
            public readonly List<StopLine> Stops = new List<StopLine>();
            public readonly List<(float At, float Degrees)> Bends = new List<(float, float)>();

            public static ShuttleRoute Create(string id, IReadOnlyList<Vector2> source)
            {
                var points = new List<Vector2>();
                foreach (var point in source)
                    if (points.Count == 0 || (point - points[points.Count - 1]).sqrMagnitude > 1e-4f) points.Add(point);
                if (points.Count < 2) return null;
                var along = new float[points.Count];
                for (var i = 1; i < points.Count; i++) along[i] = along[i - 1] + Vector2.Distance(points[i - 1], points[i]);
                var route = new ShuttleRoute
                {
                    Id = id, Source = new Vector2[source.Count], Points = points.ToArray(), Along = along, Length = along[along.Length - 1],
                    Closed = Vector2.Distance(points[0], points[points.Count - 1]) < 1f,
                };
                for (var i = 0; i < source.Count; i++) route.Source[i] = source[i];
                for (var i = 1; i + 1 < points.Count; i++)
                {
                    var into = RoutePoint(route, along[i]) - RoutePoint(route, along[i] - ShuttleBendSpanM);
                    var outOf = RoutePoint(route, along[i] + ShuttleBendSpanM) - RoutePoint(route, along[i]);
                    var degrees = Vector2.Angle(into, outOf);
                    if (degrees >= ShuttleBendDeg) route.Bends.Add((along[i], degrees));
                }
                return route;
            }

            public bool Matches(IReadOnlyList<Vector2> source)
            {
                if (source.Count != Source.Length) return false;
                for (var i = 0; i < Source.Length; i++)
                    if (Source[i] != source[i]) return false;
                return true;
            }
        }
    }
}
