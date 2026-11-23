/*
 * Watches every shuttle's battery against what it needs to get home.
 *
 * Description:
 *     On every accepted snapshot each shuttle's shortest road distance to the nearest depot is
 *     found, turned into the energy that trip needs, and a threshold is built from it:
 *
 *         threshold = distance * kWh-per-metre * safety factor + reserve
 *
 *     The threshold moves with the shuttle: it rises as the shuttle drives away from the depot and
 *     falls as it comes back. A shuttle at or below it is flagged ReturnNow; one that could not
 *     reach the depot even by spending its reserve is flagged Critical. Each flag is raised once,
 *     as an event, and held until the shuttle charges (so it cannot flap as the estimate wobbles).
 *
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Formula9.Contracts;
using UnityEngine;

namespace Formula9.World
{
    public enum BatteryRisk { Ok = 0, ReturnNow = 1, Critical = 2 }

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
        public double DistanceToDepotM;    // PositiveInfinity when closures cut the shuttle off
        public bool OffNetwork;            // distance is a straight line, not a road path
        public double TripKwh;             // energy the trip needs, before margins
        public double ReserveKwh;
        public double ThresholdKwh;        // trip * safety factor + reserve
        public bool AlreadyReturning;      // the backend already has it heading for the depot

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
        // A raised flag clears only once the battery is this fraction of capacity above the threshold
        public double ReleaseMarginFraction = 0.03;

        // Raised once when a shuttle first reaches the threshold (not for one already returning)
        public event Action<BatteryAssessment> ReturnRequired;
        // Raised once when a shuttle can no longer reach a depot above empty
        public event Action<BatteryAssessment> Critical;

        private static readonly HashSet<string> Parked = new HashSet<string> { "CHARGING", "QUEUED_FOR_CHARGE", "STANDBY", "OUT_OF_SERVICE" };

        private readonly DepotRoadGraph graph;
        private readonly Dictionary<string, BatteryRisk> held = new Dictionary<string, BatteryRisk>();
        private readonly Dictionary<string, BatteryAssessment> latest = new Dictionary<string, BatteryAssessment>();
        private string sessionId, depotKey, closureKey;

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
                held.Clear(); latest.Clear(); depotKey = closureKey = null;
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
            if (closures != closureKey)
            {
                closureKey = closures;
                graph.SetClosed(active.SelectMany(d => d.AffectedEdgeIds ?? new List<string>()));
            }
        }

        private void Assess(FleetVehicle vehicle, FullSnapshot snapshot)
        {
            var id = vehicle.VehicleId;
            held.TryGetValue(id, out var before);

            // Parked shuttles are at a depot already; a charge ends the episode
            if (Parked.Contains(vehicle.ServiceState))
            {
                held.Remove(id);
                latest.Remove(id);
                return;
            }

            var forecast = snapshot.EnergyForecasts?.FirstOrDefault(f => f.VehicleId == id);
            var reserve = forecast?.ReserveKwh ?? vehicle.BatteryCapacityKwh * ReserveFraction;
            var at = new Vector2((float)vehicle.Position.XM, (float)vehicle.Position.YM);
            var path = graph.DistanceToDepot(at, (float)(vehicle.HeadingDeg * Mathf.Deg2Rad));
            var distance = path.Reachable ? path.DistanceM : double.PositiveInfinity;

            var trip = distance * KwhPerMeter;
            var threshold = ThresholdKwh(distance, KwhPerMeter, SafetyFactor, reserve);
            var release = vehicle.BatteryCapacityKwh * ReleaseMarginFraction;

            BatteryRisk risk;
            // The backend's own forecast is trusted when it says the depot is out of reach
            if (vehicle.BatteryKwh < trip || forecast?.ReachesDepotAboveReserve == false) risk = BatteryRisk.Critical;
            else if (vehicle.BatteryKwh <= threshold) risk = BatteryRisk.ReturnNow;
            else if (before != BatteryRisk.Ok && vehicle.BatteryKwh <= threshold + release) risk = before; // hysteresis
            else risk = BatteryRisk.Ok;

            var assessment = new BatteryAssessment
            {
                VehicleId = id, Risk = risk,
                BatteryKwh = vehicle.BatteryKwh, CapacityKwh = vehicle.BatteryCapacityKwh,
                DistanceToDepotM = distance, OffNetwork = path.OffNetwork,
                TripKwh = trip, ReserveKwh = reserve, ThresholdKwh = threshold,
                AlreadyReturning = vehicle.ServiceState == "RETURNING_TO_DEPOT",
            };
            latest[id] = assessment;
            if (risk == BatteryRisk.Ok) held.Remove(id); else held[id] = risk;

            if (risk > before)
            {
                if (risk == BatteryRisk.Critical) Critical?.Invoke(assessment);
                else if (!assessment.AlreadyReturning) ReturnRequired?.Invoke(assessment);
            }
        }
    }
}

