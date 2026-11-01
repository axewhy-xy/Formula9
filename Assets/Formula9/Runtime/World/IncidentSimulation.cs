using System;
using System.Collections.Generic;
using System.Linq;
using Formula9.Contracts;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Event = Formula9.Contracts.Event;

namespace Formula9.World
{
    // Where an incident happened and what it does, as its first events say. The snapshot supplies its
    // current state.
    public sealed class IncidentRecord
    {
        public string IncidentId, EdgeId, Kind, Impact;
        public Vector2 Position, Start, End;
        public List<string> ImpactEdgeIds = new List<string>();
        public bool RequiresGuidance;
        public long? ActiveUntilTick;

        public static void Merge(IEnumerable<Event> events, IDictionary<string, IncidentRecord> records)
        {
            if (events == null) return;
            foreach (var evt in events.OrderBy(e => e.EventSequence))
                if (TryRead(evt, out var record)) records[evt.EntityId] = record;
        }

        // Only an incident's first event carries its map marker; later ones say nothing about where it is.
        public static bool TryRead(Event evt, out IncidentRecord record)
        {
            record = null;
            if (evt.EntityType != "INCIDENT" || evt.Data == null) return false;
            var marker = evt.Data["map_marker"] as JObject;
            var center = marker?["center"];
            if (center == null) return false;
            var x = center.Value<float?>("x_m"); var y = center.Value<float?>("y_m");
            if (!x.HasValue || !y.HasValue) return false;
            var impacted = (evt.Data["impact_edge_ids"] as JArray)?.Values<string>().ToList() ?? new List<string>();
            var start = marker["start"];
            var end = marker["end"];
            record = new IncidentRecord
            {
                IncidentId = evt.EntityId,
                EdgeId = marker.Value<string>("edge_id") ?? "",
                Kind = evt.Data.Value<string>("incident_type") ?? "INCIDENT",
                Impact = evt.Data.Value<string>("operational_impact") ?? (impacted.Count > 0 ? "Route may be affected" : "No effect on operations"),
                Position = new Vector2(x.Value, y.Value),
                Start = new Vector2(start?.Value<float?>("x_m") ?? x.Value, start?.Value<float?>("y_m") ?? y.Value),
                End = new Vector2(end?.Value<float?>("x_m") ?? x.Value, end?.Value<float?>("y_m") ?? y.Value),
                ImpactEdgeIds = impacted,
                RequiresGuidance = evt.Data.Value<bool?>("requires_guidance") ?? evt.EventType == "OPTIONS_READY",
                ActiveUntilTick = evt.Data.Value<long?>("active_until_tick"),
            };
            return true;
        }
    }

    // One road of the traffic backing up from an incident, from its end nearest the incident outwards.
    // StartM is how far the queue has already come by the time it reaches this road.
    public sealed class QueueLane
    {
        public string EdgeId;
        public Vector2[] Points;
        public float StartM, LengthM;
    }

    // A queued car: which lane it waits on, and how far from the head of the queue.
    public struct QueueCar
    {
        public int Lane, Paint;
        public float StopM;
    }

    public sealed class IncidentPlan
    {
        public IncidentRecord Record;
        public string RoadName;
        public Vector2[] Road;
        public Vector2 Site, Along;
        public List<QueueLane> Lanes = new List<QueueLane>();
        public List<QueueCar> Cars = new List<QueueCar>();
        // The roads on which the backend really slows the shuttle
        public List<Vector2[]> Slowed = new List<Vector2[]>();
    }

    // Turns the backend's incident into what the 3D city draws: the scene on the road, and the traffic
    // backing up along the roads leading to it. The backend models no other traffic ("No traffic
    // volume, lanes or signal timings are modelled"), so the queue is illustration: it follows the real
    // road graph and the incident's own clock, and never affects the shuttle. Only the backend moves
    // the shuttle, and only on the incident's impact edges. The background traffic keeps off the scene
    // and stops behind the queue, by way of Hazards.
    public static class IncidentSimulation
    {
        public const string Block = "ROAD_BLOCK", Accident = "ROAD_ACCIDENT";
        public const float ReachM = 140, GrowthMps = 5, LaneOffsetM = 3.5f;
        private const float WreckClearM = 12, FirstCarM = 6, CarSpacingM = 9, CarJitterM = 3;
        private const float RollInS = 3, RollInMps = 8, CreepM = 0.8f, CreepRate = 0.9f, CreepWaveM = 0.06f;
        private const int MaxLanes = 16, MaxCars = 48, Paints = 5;
        private const float ShuttleOnRoadM = 8;
        // The wrecks, van and cones stretch this far along the road either side of the site
        private const float SceneReachM = 14;

        public static IncidentPlan Plan(IncidentRecord record, IReadOnlyList<DraftRoad> network, FullSnapshot snapshot)
        {
            var road = network?.FirstOrDefault(r => r.EdgeId == record.EdgeId);
            var points = road?.Points ?? RoutePath(snapshot, record.EdgeId) ?? new[] { record.Start, record.End };
            var length = Length(points);
            var site = At(points, length / 2, out var along);
            var plan = new IncidentPlan { Record = record, RoadName = road?.Name, Road = points, Site = site, Along = along };
            // A closure stops traffic where the road begins; an accident part way along it
            var head = record.Kind == Block ? 0 : Mathf.Max(0, length / 2 - WreckClearM);
            if (head > 0) AddLane(plan, record.EdgeId, Between(points, 0, head).Reverse().ToArray(), 0);
            if (road != null) Walk(plan, road, head, network);
            foreach (var id in record.RequiresGuidance ? Enumerable.Empty<string>() : record.ImpactEdgeIds)
            {
                var path = network?.FirstOrDefault(r => r.EdgeId == id)?.Points ?? RoutePath(snapshot, id);
                if (path != null) plan.Slowed.Add(path);
            }
            PlaceCars(plan);
            return plan;
        }

        // Breadth first up the roads whose traffic flows into this one, until the queue's reach runs out.
        private static void Walk(IncidentPlan plan, DraftRoad first, float head, IReadOnlyList<DraftRoad> network)
        {
            var seen = new HashSet<string> { first.EdgeId };
            var pending = new Queue<(DraftRoad Road, float StartM)>();
            pending.Enqueue((first, head));
            while (pending.Count > 0)
            {
                var (road, startM) = pending.Dequeue();
                // Not the road's own other direction: that traffic is leaving
                var feeders = network.Where(r => r.Target == road.Source && r.Source != road.Target && !r.Closed)
                    .OrderBy(r => r.EdgeId, StringComparer.Ordinal);
                foreach (var feeder in feeders)
                {
                    if (plan.Lanes.Count >= MaxLanes || !seen.Add(feeder.EdgeId)) continue;
                    var length = Length(feeder.Points);
                    AddLane(plan, feeder.EdgeId, Between(feeder.Points, Mathf.Max(0, length - (ReachM - startM)), length).Reverse().ToArray(), startM);
                    if (startM + length < ReachM) pending.Enqueue((feeder, startM + length));
                }
            }
        }

        private static void AddLane(IncidentPlan plan, string edgeId, Vector2[] points, float startM)
        {
            var length = Length(points);
            if (length > 0.01f) plan.Lanes.Add(new QueueLane { EdgeId = edgeId, Points = points, StartM = startM, LengthM = length });
        }

        private static void PlaceCars(IncidentPlan plan)
        {
            var random = new System.Random(plan.Record.IncidentId.Aggregate(17, (hash, c) => unchecked(hash * 31 + c)));
            for (var lane = 0; lane < plan.Lanes.Count; lane++)
            {
                var from = plan.Lanes[lane].StartM;
                var end = from + plan.Lanes[lane].LengthM;
                for (var stop = from + FirstCarM; stop < end && plan.Cars.Count < MaxCars; stop += CarSpacingM + (float)random.NextDouble() * CarJitterM)
                    plan.Cars.Add(new QueueCar { Lane = lane, Paint = random.Next(Paints), StopM = stop });
            }
        }

        // How far the traffic has backed up, seconds of simulation after the incident.
        public static float QueueM(float elapsedS) => Mathf.Clamp(elapsedS * GrowthMps, 0, ReachM);

        // What the background traffic must keep off right now: the closed road or the accident scene, in
        // either direction, and as much of the queue as has formed, in the direction it is waiting.
        public static List<TrafficHazard> Hazards(IncidentPlan plan, float elapsedS)
        {
            var hazards = new List<TrafficHazard>();
            var length = Length(plan.Road);
            var scene = plan.Record.Kind == Block ? plan.Road : Between(plan.Road, Mathf.Max(0, length / 2 - SceneReachM), Mathf.Min(length, length / 2 + SceneReachM));
            hazards.Add(new TrafficHazard { Path = scene, BothWays = true });
            var queue = QueueM(elapsedS);
            foreach (var lane in plan.Lanes)
            {
                var formed = Mathf.Clamp(queue - lane.StartM, 0, lane.LengthM);
                if (formed > 0) hazards.Add(new TrafficHazard { Path = Between(lane.Points, 0, formed).Reverse().ToArray() });
            }
            return hazards;
        }

        // A car rolls up to the back of the queue, stops, then edges forward with it. False until it comes.
        public static bool CarPose(QueueCar car, QueueLane lane, float elapsedS, out Vector2 at, out Vector2 travel)
        {
            at = travel = default;
            var left = car.StopM / GrowthMps - elapsedS;
            if (left > RollInS) return false;
            var creep = left > 0 ? 0 : CreepM * (1 + Mathf.Sin(elapsedS * CreepRate - car.StopM * CreepWaveM));
            var point = At(lane.Points, car.StopM - lane.StartM + Mathf.Max(0, left) * RollInMps - creep, out var outwards);
            travel = -outwards;
            // Singapore drives on the left, which also keeps the queue beside a shuttle on the road's centre
            at = point + new Vector2(-travel.y, travel.x) * LaneOffsetM;
            return true;
        }

        // Whether the incident is still on the road. This outlives the operator's case: a shuttle can
        // finish its detour around a closure that has not been cleared.
        public static bool IsLive(IncidentRecord record, FullSnapshot snapshot)
        {
            var incident = snapshot.Incidents.FirstOrDefault(i => i.IncidentId == record.IncidentId);
            if (incident == null) return false;
            if (record.Kind == Block) return ClearsAt(record, snapshot, incident) is long until ? snapshot.Tick < until : Closure(record, snapshot, incident) != null;
            return !Settled(incident) && (!(record.ActiveUntilTick is long end) || snapshot.Tick < end);
        }

        private static bool Settled(Incident incident) => incident.State == "RESOLVED" || incident.State == "UNRESOLVED";

        private static Disruption Closure(IncidentRecord record, FullSnapshot snapshot, Incident incident) =>
            snapshot.Disruptions.FirstOrDefault(d => d.AffectedEdgeIds.Contains(record.EdgeId) && d.ActiveFromTick >= incident.DetectedTick);

        private static long? ClearsAt(IncidentRecord record, FullSnapshot snapshot, Incident incident) =>
            Closure(record, snapshot, incident)?.ActiveUntilTick ?? record.ActiveUntilTick;

        public static float SecondsPerTick(FullSnapshot snapshot) => snapshot.Tick > 0 ? (float)(snapshot.SimulationTimeS / snapshot.Tick) : 0.05f;

        public static float ElapsedS(IncidentRecord record, FullSnapshot snapshot)
        {
            var incident = snapshot.Incidents.FirstOrDefault(i => i.IncidentId == record.IncidentId);
            return incident == null ? 0 : Mathf.Max(0, (snapshot.Tick - incident.DetectedTick) * SecondsPerTick(snapshot));
        }

        // Is this shuttle crawling because of this incident? Only true on a road the backend slows.
        public static bool IsSlowing(IncidentPlan plan, FleetVehicle vehicle, FullSnapshot snapshot)
        {
            var incident = snapshot.Incidents.FirstOrDefault(i => i.IncidentId == plan.Record.IncidentId);
            if (incident?.VehicleId != vehicle.VehicleId || vehicle.SpeedMps <= 0) return false;
            var at = new Vector2((float)vehicle.Position.XM, (float)vehicle.Position.YM);
            return plan.Slowed.Any(path => Distance(path, at) <= ShuttleOnRoadM);
        }

        // One line about a live incident, for the status over the 3D view.
        public static string Line(IncidentPlan plan, FullSnapshot snapshot)
        {
            var record = plan.Record;
            var kind = record.Kind == Block ? "Road block" : record.Kind == Accident ? "Road accident" : "Incident";
            var road = string.IsNullOrEmpty(plan.RoadName) ? "" : " · " + plan.RoadName;
            var incident = snapshot.Incidents.First(i => i.IncidentId == record.IncidentId);
            // The event's words were true when it was detected; a decision has been made since
            var impact = record.RequiresGuidance && incident.State != "OPTIONS_READY" ? "Road closed — operator has decided" : record.Impact;
            var clears = ClearsAt(record, snapshot, incident) is long until ? $" · clears in {Mathf.CeilToInt(Mathf.Max(0, until - snapshot.Tick) * SecondsPerTick(snapshot))} s" : "";
            var slowed = snapshot.Vehicles.FirstOrDefault(v => IsSlowing(plan, v, snapshot));
            var crawl = slowed == null ? "" : $" · {slowed.VehicleId} slowed to {slowed.SpeedMps:0.#} m/s";
            return $"{kind}{road} · {impact}{clears}{crawl}";
        }

        private static Vector2[] RoutePath(FullSnapshot snapshot, string edgeId)
        {
            foreach (var route in snapshot.Routes)
            {
                var index = route.DirectedEdgeIds?.IndexOf(edgeId) ?? -1;
                if (index >= 0 && index < route.EdgePathsM.Count) return route.EdgePathsM[index].Select(p => new Vector2((float)p.XM, (float)p.YM)).ToArray();
            }
            return null;
        }

        public static float Length(IReadOnlyList<Vector2> points)
        {
            var total = 0f;
            for (var i = 1; i < points.Count; i++) total += Vector2.Distance(points[i - 1], points[i]);
            return total;
        }

        // The point this far along a line, kept on it, and the way the line runs there.
        public static Vector2 At(IReadOnlyList<Vector2> points, float distance, out Vector2 along)
        {
            along = points.Count > 1 ? (points[1] - points[0]).normalized : Vector2.up;
            var left = Mathf.Max(0, distance);
            for (var i = 1; i < points.Count; i++)
            {
                var step = Vector2.Distance(points[i - 1], points[i]);
                if (step <= 0) continue;
                along = (points[i] - points[i - 1]) / step;
                if (left <= step) return points[i - 1] + along * left;
                left -= step;
            }
            return points[points.Count - 1];
        }

        // The part of a line between two distances along it.
        public static Vector2[] Between(IReadOnlyList<Vector2> points, float from, float to)
        {
            var result = new List<Vector2> { At(points, from, out _) };
            var walked = 0f;
            for (var i = 1; i < points.Count; i++)
            {
                walked += Vector2.Distance(points[i - 1], points[i]);
                if (walked > from && walked < to) result.Add(points[i]);
            }
            result.Add(At(points, to, out _));
            return result.ToArray();
        }

        private static float Distance(IReadOnlyList<Vector2> points, Vector2 at)
        {
            var best = float.MaxValue;
            for (var i = 1; i < points.Count; i++)
            {
                var along = points[i] - points[i - 1];
                var share = along.sqrMagnitude > 0 ? Mathf.Clamp01(Vector2.Dot(at - points[i - 1], along) / along.sqrMagnitude) : 0;
                best = Mathf.Min(best, Vector2.Distance(at, points[i - 1] + along * share));
            }
            return best;
        }
    }
}
