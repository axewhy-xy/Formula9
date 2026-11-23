/*
 * Unit tests for DepotRoadGraph.cs and ShuttleBatteryMonitor.cs.
 */

using System.Collections.Generic;
using Formula9.Backend;
using Formula9.Contracts;
using Formula9.World;
using NUnit.Framework;
using UnityEngine;

namespace Formula9.Tests
{
    public class ShuttleBatteryMonitorTests
    {
        // Depot at (0,0). R1 is two-way, (0,0)-(1000,0). R2 goes one way north, (1000,0)-(1000,1000).
        // R3 goes one way from there back to the depot, so from R2 the only way home is the long way round.
        private static List<CityRoad> Roads() => new List<CityRoad>
        {
            Road("R1", 1, 2, false, (0, 0), (1000, 0)),
            Road("R2", 2, 3, true, (1000, 0), (1000, 1000)),
            Road("R3", 3, 1, true, (1000, 1000), (0, 0)),
        };

        private static CityRoad Road(string id, long from, long to, bool oneway, params (double, double)[] points) =>
            new CityRoad { Id = id, FromNode = from, ToNode = to, Oneway = oneway, Lanes = 1, SpeedKph = 50, Points = new List<(double X, double Y)>(points) };

        private static DepotRoadGraph Graph()
        {
            var graph = new DepotRoadGraph(Roads());
            graph.SetDepots(new[] { Vector2.zero });
            return graph;
        }

        [Test]
        public void StraightRoad_IsTheRemainingLength_WhicheverWayItFaces()
        {
            var result = Graph().DistanceToDepot(new Vector2(500, 0));
            Assert.IsTrue(result.Reachable);
            Assert.AreEqual(500f, result.DistanceM, 1f);
        }

        [Test]
        public void OneWayRoad_ForcesTheLongWayRound()
        {
            var result = Graph().DistanceToDepot(new Vector2(1000, 500), Mathf.PI / 2);
            Assert.AreEqual(500f + 1414.2f, result.DistanceM, 2f);
        }

        [Test]
        public void Closure_CutsTheShuttleOff_AndReopeningRestoresIt()
        {
            var graph = Graph();
            graph.SetClosed(new[] { "R3" });
            var cut = graph.DistanceToDepot(new Vector2(1000, 500), Mathf.PI / 2);
            Assert.IsFalse(cut.Reachable);
            graph.SetClosed(new string[0]);
            Assert.IsTrue(graph.DistanceToDepot(new Vector2(1000, 500), Mathf.PI / 2).Reachable);
        }

        [Test]
        public void NearADepot_ItIsTheStraightLine()
        {
            var result = Graph().DistanceToDepot(new Vector2(20, 5));
            Assert.AreEqual(20.6f, result.DistanceM, 0.5f);
        }

        [Test]
        public void FarFromEveryRoad_IsOffNetwork()
        {
            var result = Graph().DistanceToDepot(new Vector2(5000, 5000));
            Assert.IsTrue(result.OffNetwork);
        }

        [Test]
        public void Threshold_AddsTheTripToTheReserve()
        {
            Assert.AreEqual(6.625, ShuttleBatteryMonitor.ThresholdKwh(1000, 0.0005, 1.25, 6), 1e-9);
        }

        private static FullSnapshot Snapshot(string state, double batteryKwh, double x = 500, long tick = 1) => new FullSnapshot
        {
            SessionId = "s1", Tick = tick,
            Depots = new List<Depot> { new Depot { DepotId = "depot-1", Position = new Position { XM = 0, YM = 0 } } },
            Vehicles = new List<FleetVehicle>
            {
                new FleetVehicle { VehicleId = "shuttle-01", ServiceState = state, BatteryKwh = batteryKwh, BatteryCapacityKwh = 40,
                                   HeadingDeg = 180, Position = new Position { XM = x, YM = 0 } },
            },
        };

        // 500 m out: trip 0.25 kWh, threshold 0.25 * 1.25 + 6 (15% of 40) = 6.3125
        [Test]
        public void RaisesOnce_WhenTheBatteryReachesTheThreshold()
        {
            var monitor = new ShuttleBatteryMonitor(Graph());
            var raised = new List<BatteryAssessment>();
            monitor.ReturnRequired += raised.Add;

            monitor.OnSnapshot(Snapshot("IN_SERVICE", 6.5));
            Assert.AreEqual(0, raised.Count);
            monitor.OnSnapshot(Snapshot("IN_SERVICE", 6.3));
            monitor.OnSnapshot(Snapshot("IN_SERVICE", 6.2));
            Assert.AreEqual(1, raised.Count);
            Assert.AreEqual(BatteryRisk.ReturnNow, raised[0].Risk);
        }

        [Test]
        public void ThresholdMoves_WithTheDistanceToTheDepot()
        {
            var monitor = new ShuttleBatteryMonitor(Graph());
            monitor.OnSnapshot(Snapshot("IN_SERVICE", 20, x: 900));
            var far = monitor.Latest["shuttle-01"].ThresholdKwh;
            monitor.OnSnapshot(Snapshot("IN_SERVICE", 20, x: 100));
            Assert.Greater(far, monitor.Latest["shuttle-01"].ThresholdKwh);
        }

        [Test]
        public void Critical_WhenTheTripNeedsMoreThanIsLeft()
        {
            var monitor = new ShuttleBatteryMonitor(Graph());
            var critical = 0;
            monitor.Critical += _ => critical++;
            monitor.OnSnapshot(Snapshot("IN_SERVICE", 0.2));   // trip needs 0.25
            Assert.AreEqual(1, critical);
        }

        [Test]
        public void AShuttleAlreadyReturning_DoesNotRaiseAgain()
        {
            var monitor = new ShuttleBatteryMonitor(Graph());
            var raised = 0;
            monitor.ReturnRequired += _ => raised++;
            monitor.OnSnapshot(Snapshot("RETURNING_TO_DEPOT", 6.0));
            Assert.AreEqual(0, raised);
            Assert.IsTrue(monitor.Latest["shuttle-01"].AlreadyReturning);
        }

        [Test]
        public void ChargingClearsTheFlag_SoItRaisesAgainNextTime()
        {
            var monitor = new ShuttleBatteryMonitor(Graph());
            var raised = 0;
            monitor.ReturnRequired += _ => raised++;
            monitor.OnSnapshot(Snapshot("IN_SERVICE", 6.0));
            monitor.OnSnapshot(Snapshot("CHARGING", 20));
            monitor.OnSnapshot(Snapshot("IN_SERVICE", 6.0));
            Assert.AreEqual(2, raised);
        }
    }
}
