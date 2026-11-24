/*
 * Watches every shuttle's battery against what it needs to get home.
 *
 * Author: Ang Yu Xuan
 * Date Created: 18/11/2026
 * Last Modified: 24/11/2026
 *
 * Description:
 *     On every accepted snapshot each shuttle's shortest road distance to the nearest depot is
 *     found, turned into the energy that trip needs, and a threshold is built from it:
 *
 *         threshold = distance * kWh-per-metre * safety factor + reserve
 *
 *     The threshold moves with the shuttle: it rises as the shuttle drives away from the depot and
 *     falls as it comes back. A shuttle at or below it is flagged ReturnNow; one that could not
 *     reach the depot even by spending its reserve is flagged Critical.
 *
 *     Each warning is raised once per low-battery episode. An episode starts when a shuttle first
 *     reaches ReturnNow and ends only when its battery has risen again (it has charged) or it parks
 *     at a depot. Until then the shuttle stays at least ReturnNow, however the estimate moves, and a
 *     warning already raised is not raised again. A worse one (ReturnNow to Critical) is.
 *
 *     When the distance cannot be found (no depot known, or no road path to one) the shuttle is
 *     Unknown, never Critical: the monitor then only checks the battery against the reserve and
 *     the backend's own forecast.
 *
 *     This is a plain class wired like BatteryWatch (see ShuttleBatteryService). The backend owns
 *     the real energy model and its own safety return; this is the client-side view that tells the
 *     operator early.

 *     Situation	                                        Result
 *     Distance known, battery healthy	                    Ok
 *     Distance known, can't reach the depot on its charge	Critical
 *     Distance unknown, battery above the reserve	        Unknown, with no warning
 *     Distance unknown, battery at or below the reserve	ReturnNow, with a warning
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Formula9.Contracts;
using UnityEngine;

namespace Formula9.World
{
    // Unknown: the distance to a depot could not be found, so only the reserve and forecast apply
    public enum BatteryRisk { Unknown = -1, Ok = 0, ReturnNow = 1, Critical = 2 }

    // -----------------------------------------------------------------------------------
    // Class: BatteryAssessment
    // -----------------------------------------------------------------------------------
    // Description:
    //     One shuttle's battery check from one snapshot. Read-only data for the UI.
    public sealed class BatteryAssessment
    {
        public string VehicleId;
        public BatteryRisk Risk;
        public double BatteryKwh, CapacityKwh;
        public double DistanceToDepotM;    // NaN when it could not be found
        public bool OffNetwork;            // distance is a straight line, not a road path
        public double TripKwh;             // energy the trip needs, before margins; NaN when unknown
        public double ReserveKwh;
        public double ThresholdKwh;        // trip * safety factor + reserve; just the reserve when the distance is unknown
        public bool AlreadyReturning;      // the backend already has it heading for the depot

        public bool DistanceKnown => !double.IsNaN(DistanceToDepotM);
        // Charge left above the threshold; negative once the shuttle should be heading home
        public double MarginKwh => BatteryKwh - ThresholdKwh;
        public double BatteryPercent => CapacityKwh > 0 ? 100 * BatteryKwh / CapacityKwh : 0;
        // For drawing the moving threshold on a gauge
        public double ThresholdPercent => CapacityKwh > 0 ? Math.Min(100, 100 * ThresholdKwh / CapacityKwh) : 100;
    }

    // -----------------------------------------------------------------------------------
    // Class: ShuttleBatteryMonitor
    // -----------------------------------------------------------------------------------
    public sealed class ShuttleBatteryMonitor
    {
        // 0.5 kWh per km. A placeholder from the energy blueprint
        public const double DefaultKwhPerMeter = 0.0005;
        public const double DefaultReserveFraction = 0.15;

        public double KwhPerMeter = DefaultKwhPerMeter;
        // Covers stops, lights, traffic and detours that make the real trip cost more than the path length
        public double SafetyFactor = 1.25;
        // Used only when the snapshot carries no forecast with the backend's own reserve
        public double ReserveFraction = DefaultReserveFraction;
        // An episode ends when the battery is this fraction of capacity above its lowest point in it.
        // Only charging raises a battery, so this is "it has charged", with room for sensor noise.
        public double ReleaseMarginFraction = 0.03;

        // Raised once per episode when a shuttle first reaches the threshold (not for one already returning)
        public event Action<BatteryAssessment> ReturnRequired;
        // Raised once per episode when a shuttle can no longer reach a depot above empty
        public event Action<BatteryAssessment> Critical;
        // Something the operator cannot see but should know, for example closures that change nothing
        public event Action<string> Diagnostic;
        // Closures name backend edges this monitor has no geometry for. Whoever can fetch it answers
        // with SetEdgePaths (an empty answer is fine: it says there is none).
        public event Action<IReadOnlyCollection<string>> EdgePathsNeeded;

        private static readonly HashSet<string> Parked = new HashSet<string> { "CHARGING", "QUEUED_FOR_CHARGE", "STANDBY", "OUT_OF_SERVICE" };

        // A low-battery episode: the lowest charge seen in it, and the worst warning already raised
        private sealed class Episode { public double LowestKwh; public BatteryRisk Raised; }

        private readonly DepotRoadGraph graph;
        private readonly Dictionary<string, Episode> episodes = new Dictionary<string, Episode>();
        private readonly Dictionary<string, BatteryAssessment> latest = new Dictionary<string, BatteryAssessment>();
        private string sessionId, depotKey, closureKey;
        // The closures in force, the geometry known for backend edges, and the edges already asked about
        private List<string> activeIds = new List<string>();
        private readonly Dictionary<string, Vector2[]> edgePaths = new Dictionary<string, Vector2[]>();
        private readonly HashSet<string> asked = new HashSet<string>();

        public IReadOnlyDictionary<string, BatteryAssessment> Latest => latest;

        public ShuttleBatteryMonitor(DepotRoadGraph graph)
        {
            this.graph = graph ?? throw new ArgumentNullException(nameof(graph));
        }

        // -----------------------------------------------------------------------------------
        // Function: ThresholdKwh
        // -----------------------------------------------------------------------------------
        // Description:
        //     The charge a shuttle must still have to be this far from a depot. Pure, so it is
        //     tested without a snapshot.
        public static double ThresholdKwh(double distanceM, double kwhPerMeter, double safetyFactor, double reserveKwh) =>
            distanceM * kwhPerMeter * safetyFactor + reserveKwh;

        // -----------------------------------------------------------------------------------
        // Function: OnSnapshot
        // -----------------------------------------------------------------------------------
        // Description:
        //     Checks every shuttle in a snapshot. Hook to SessionController.SnapshotAccepted.
        public void OnSnapshot(FullSnapshot snapshot)
        {
            if (snapshot?.Vehicles == null) return;
            if (snapshot.SessionId != sessionId)
            {
                sessionId = snapshot.SessionId;
                episodes.Clear(); latest.Clear(); depotKey = closureKey = null;
                activeIds = new List<string>(); edgePaths.Clear(); asked.Clear();
            }
            SyncGraph(snapshot);
            foreach (var vehicle in snapshot.Vehicles) Assess(vehicle, snapshot);
        }

        private void SyncGraph(FullSnapshot snapshot)
        {
            var depots = snapshot.Depots ?? new List<Depot>();
            var key = string.Join(";", depots.Select(d => $"{d.DepotId}:{d.Position.XM}:{d.Position.YM}"));
            if (key != depotKey)
            {
                depotKey = key;
                graph.SetDepots(depots.Select(d => new Vector2((float)d.Position.XM, (float)d.Position.YM)));
            }

            // Closures change the shortest way home, so the graph follows the disruptions in force
            var active = (snapshot.Disruptions ?? new List<Disruption>())
                .Where(d => d.ActiveFromTick <= snapshot.Tick && (d.ActiveUntilTick == null || snapshot.Tick < d.ActiveUntilTick))
                .ToList();
            var closures = string.Join(";", active.Select(d => $"{d.DisruptionId}:{d.AggregateRevision}"));
            if (closures == closureKey) return;
            closureKey = closures;

            Debug.Log($"[BatteryMonitor] disruptions: {snapshot.Disruptions?.Count ?? 0} total, {active.Count} active; closed edge ids: {string.Join(", ", active.SelectMany(d => d.AffectedEdgeIds ?? new List<string>()))}");


            activeIds = active.SelectMany(d => d.AffectedEdgeIds ?? new List<string>()).Distinct().ToList();
            ApplyClosures();
        }

        // -----------------------------------------------------------------------------------
        // Function: SetEdgePaths
        // -----------------------------------------------------------------------------------
        // Description:
        //     Gives the monitor the geometry of backend edges it asked for through EdgePathsNeeded, and
        //     applies the closures again with it. The backend names closed edges by its own IDs, which
        //     are not city road IDs, so the roads are found by where the edges run.
        //
        // Args:
        //     paths (IReadOnlyDictionary<string, Vector2[]>): Edge ID to its polyline, in local metres.
        public void SetEdgePaths(IReadOnlyDictionary<string, Vector2[]> paths)
        {
            foreach (var pair in paths) edgePaths[pair.Key] = pair.Value;
            ApplyClosures();
        }

        // Closes what the closures in force name: a road by ID where the ID is one, else the roads along the edge's path
        private void ApplyClosures()
        {
            var along = new List<Vector2[]>();
            var missing = new List<string>();
            foreach (var id in activeIds)
            {
                if (graph.NamesRoad(id)) continue;
                if (edgePaths.TryGetValue(id, out var path)) along.Add(path);
                else if (asked.Add(id)) missing.Add(id);
            }
            var matched = graph.SetClosures(activeIds, along);

            Debug.Log($"[BatteryMonitor] closures named: {activeIds.Count}, graph edges closed: {matched}");


            // Ask for the geometry first; the closures are applied again when it arrives
            if (missing.Count > 0) { EdgePathsNeeded?.Invoke(missing); return; }
            // Neither an ID of the graph nor a path along one of its roads: the closures change nothing
            if (activeIds.Count > 0 && matched == 0)
                Diagnostic?.Invoke($"Road closures name {activeIds.Count} edge(s), for example '{activeIds[0]}', but none could be matched to a road of the graph, " +
                                   $"by ID (its roads look like '{graph.SampleRoadKey}') or by position. Closures are not changing the shortest path home.");
        }

        private void Assess(FleetVehicle vehicle, FullSnapshot snapshot)
        {
            var id = vehicle.VehicleId;

            // Parked shuttles are at a depot already; a charge ends the episode
            if (Parked.Contains(vehicle.ServiceState))
            {
                episodes.Remove(id);
                latest.Remove(id);
                return;
            }

            var forecast = snapshot.EnergyForecasts?.FirstOrDefault(f => f.VehicleId == id);
            var reserve = forecast?.ReserveKwh ?? vehicle.BatteryCapacityKwh * ReserveFraction;

            // The way home. With no depot, or no road path to one, the distance is unknown (not "infinite")
            var path = new DepotRoadGraph.Result();
            if (graph.HasDepots)
                path = graph.DistanceToDepot(new Vector2((float)vehicle.Position.XM, (float)vehicle.Position.YM), (float)(vehicle.HeadingDeg * Mathf.Deg2Rad));
            var known = graph.HasDepots && path.Reachable;
            var distance = known ? path.DistanceM : double.NaN;
            var trip = distance * KwhPerMeter;
            var threshold = known ? ThresholdKwh(distance, KwhPerMeter, SafetyFactor, reserve) : reserve;

            BatteryRisk computed;
            if (known && vehicle.BatteryKwh < trip) computed = BatteryRisk.Critical;
            else if (vehicle.BatteryKwh <= threshold) computed = BatteryRisk.ReturnNow;
            else computed = known ? BatteryRisk.Ok : BatteryRisk.Unknown;
            // The backend's flag means it cannot reach a depot AND keep its reserve: that is ReturnNow, not Critical
            if (forecast?.ReachesDepotAboveReserve == false && computed < BatteryRisk.ReturnNow) computed = BatteryRisk.ReturnNow;

            episodes.TryGetValue(id, out var episode);
            if (episode != null)
            {
                episode.LowestKwh = Math.Min(episode.LowestKwh, vehicle.BatteryKwh);
                // Only charging raises a battery, so a rise ends the episode
                if (vehicle.BatteryKwh >= episode.LowestKwh + vehicle.BatteryCapacityKwh * ReleaseMarginFraction)
                {
                    episodes.Remove(id);
                    episode = null;
                }
            }
            // Within an episode the shuttle never improves below the worst state already reached
            var risk = computed;
            if (episode != null && risk < episode.Raised)
                risk = episode.Raised;

            var assessment = new BatteryAssessment
            {
                VehicleId = id, Risk = risk,
                BatteryKwh = vehicle.BatteryKwh, CapacityKwh = vehicle.BatteryCapacityKwh,
                DistanceToDepotM = distance, OffNetwork = known && path.OffNetwork,
                TripKwh = trip, ReserveKwh = reserve, ThresholdKwh = threshold,
                AlreadyReturning = vehicle.ServiceState == "RETURNING_TO_DEPOT",
            };
            latest[id] = assessment;

            if (risk < BatteryRisk.ReturnNow) return;
            if (episode == null) episodes[id] = episode = new Episode { LowestKwh = vehicle.BatteryKwh };
            if (risk <= episode.Raised) return;
            episode.Raised = risk;
            if (risk == BatteryRisk.Critical) Critical?.Invoke(assessment);
            else if (!assessment.AlreadyReturning) ReturnRequired?.Invoke(assessment);
        }
    }
}
