/*
 * Unit tests for DepotRoadGraph.cs and ShuttleBatteryMonitor.cs.
 *
 * Author: Ang Yu Xuan
 * Date Created: 18/11/2026
 * Last Modified: 24/11/2026
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

        // These closure tests use the key format the graph builds itself ("R3", "R3|1"). They prove the
        // mechanism, not that the backend's edge IDs look like that: see the Diagnostic test below.
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
        public void SetClosed_SaysHowManyEdgesItClosed()
        {
            var graph = Graph();
            Assert.AreEqual(1, graph.SetClosed(new[] { "R3" }), "a one-way road is one edge");
            Assert.AreEqual(2, graph.SetClosed(new[] { "R1" }), "a two-way road is two edges");
            Assert.AreEqual(1, graph.SetClosed(new[] { "R1|-1" }), "one direction of it");
            Assert.AreEqual(0, graph.SetClosed(new[] { "edge-2" }), "an ID that names no road");
        }

        // --- Closures the backend names by its own edge IDs: found by where its edges run ---

        [Test]
        public void ClosedPath_ClosesTheRoadsAlongIt_NotARoadCrossingIt()
        {
            var roads = Roads();
            roads.Add(Road("R4", 4, 5, false, (500, -200), (500, 200)));   // crosses R1 at its middle
            var graph = new DepotRoadGraph(roads);
            graph.SetDepots(new[] { Vector2.zero });
            var closedEdges = graph.SetClosures(null, new[] { new[] { new Vector2(400, 0), new Vector2(600, 0) } });
            Assert.AreEqual(2, closedEdges, "both directions of R1, and nothing else");
        }

        [Test]
        public void ClosedPath_CutsTheShuttleOff()
        {
            var graph = Graph();
            graph.SetClosures(null, new[] { new[] { new Vector2(900, 900), new Vector2(100, 100) } });   // along R3
            Assert.IsFalse(graph.DistanceToDepot(new Vector2(1000, 500), Mathf.PI / 2).Reachable);
        }

        [Test]
        public void ClosedPath_FarFromEveryRoad_ClosesNothing()
        {
            Assert.AreEqual(0, Graph().SetClosures(null, new[] { new[] { new Vector2(3000, 3000), new Vector2(3100, 3000) } }));
        }

        [Test]
        public void NearADepot_ItIsTheStraightLine()
        {
            var result = Graph().DistanceToDepot(new Vector2(20, 5));
            Assert.AreEqual(20.6f, result.DistanceM, 0.5f);
        }

        // The snap radius is 40 m: a shuttle 39 m from a road is on the network, and one 41 m away is not
        [Test]
        public void SnapRadius_IsFortyMetres()
        {
            var graph = Graph();
            Assert.IsFalse(graph.DistanceToDepot(new Vector2(500, 39)).OffNetwork, "39 m from R1 is on the road");
            Assert.IsTrue(graph.DistanceToDepot(new Vector2(500, 41)).OffNetwork, "41 m from R1 is off the network");
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

        private static FullSnapshot Snapshot(string state, double batteryKwh, double x = 500, long tick = 1, double y = 0, double heading = 180, bool depot = true) => new FullSnapshot
        {
            SessionId = "s1", Tick = tick,
            Depots = depot ? new List<Depot> { new Depot { DepotId = "depot-1", Position = new Position { XM = 0, YM = 0 } } } : new List<Depot>(),
            Vehicles = new List<FleetVehicle>
            {
                new FleetVehicle { VehicleId = "shuttle-01", ServiceState = state, BatteryKwh = batteryKwh, BatteryCapacityKwh = 40,
                                   HeadingDeg = heading, Position = new Position { XM = x, YM = y } },
            },
        };

        private static Disruption Closure(params string[] edgeIds) =>
            new Disruption { DisruptionId = "d1", AggregateRevision = 1, ActiveFromTick = 0, ActiveUntilTick = null, AffectedEdgeIds = new List<string>(edgeIds) };

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

        // --- No distance: unknown, never Critical ---

        [Test]
        public void NoDepotInTheSnapshot_IsUnknown_NotCritical()
        {
            var monitor = new ShuttleBatteryMonitor(Graph());
            var critical = 0; var raised = 0;
            monitor.Critical += _ => critical++;
            monitor.ReturnRequired += _ => raised++;
            monitor.OnSnapshot(Snapshot("IN_SERVICE", 20, depot: false));
            var assessment = monitor.Latest["shuttle-01"];
            Assert.AreEqual(BatteryRisk.Unknown, assessment.Risk);
            Assert.IsFalse(assessment.DistanceKnown);
            Assert.AreEqual(0, critical + raised);
        }

        [Test]
        public void ShuttleWithNoPathToADepot_IsUnknown_NotCritical()
        {
            var monitor = new ShuttleBatteryMonitor(Graph());
            var snapshot = Snapshot("IN_SERVICE", 20, x: 1000, y: 500, heading: 90);
            snapshot.Disruptions = new List<Disruption> { Closure("R3") };   // the only way home
            var critical = 0;
            monitor.Critical += _ => critical++;
            monitor.OnSnapshot(snapshot);
            Assert.AreEqual(BatteryRisk.Unknown, monitor.Latest["shuttle-01"].Risk);
            Assert.AreEqual(0, critical);
        }

        [Test]
        public void WithoutADistance_TheReserveStillRaisesTheWarning()
        {
            var monitor = new ShuttleBatteryMonitor(Graph());
            var raised = new List<BatteryAssessment>();
            monitor.ReturnRequired += raised.Add;
            monitor.OnSnapshot(Snapshot("IN_SERVICE", 5.0, depot: false));   // under the 6 kWh reserve
            Assert.AreEqual(1, raised.Count);
            Assert.IsFalse(raised[0].DistanceKnown);
        }

        // --- The backend's forecast ---

        [Test]
        public void ForecastSayingItWillNotKeepItsReserve_IsReturnNow_NotCritical()
        {
            var monitor = new ShuttleBatteryMonitor(Graph());
            var critical = 0; var raised = 0;
            monitor.Critical += _ => critical++;
            monitor.ReturnRequired += _ => raised++;
            var snapshot = Snapshot("IN_SERVICE", 20);   // the estimate alone says Ok
            snapshot.EnergyForecasts = new List<EnergyForecast> { new EnergyForecast { VehicleId = "shuttle-01", ReserveKwh = 6, ReachesDepotAboveReserve = false } };
            monitor.OnSnapshot(snapshot);
            Assert.AreEqual(BatteryRisk.ReturnNow, monitor.Latest["shuttle-01"].Risk);
            Assert.AreEqual(1, raised);
            Assert.AreEqual(0, critical);
        }

        // --- One warning per episode ---

        [Test]
        public void Critical_IsRaisedOnce_EvenIfTheEstimateImprovesAndWorsensAgain()
        {
            var monitor = new ShuttleBatteryMonitor(Graph());
            var critical = 0;
            monitor.Critical += _ => critical++;
            monitor.OnSnapshot(Snapshot("IN_SERVICE", 0.4, x: 900));    // trip 0.45: Critical
            monitor.OnSnapshot(Snapshot("IN_SERVICE", 0.4, x: 100));    // near the depot: the estimate improves ...
            Assert.AreEqual(BatteryRisk.Critical, monitor.Latest["shuttle-01"].Risk, "... but the state never improves below the worst reached");
            monitor.OnSnapshot(Snapshot("IN_SERVICE", 0.35, x: 900));   // Critical again
            Assert.AreEqual(1, critical);
        }

        [Test]
        public void TheFlagIsHeld_WhileTheShuttleDrivesHomeAndAwayAgain_UntilItCharges()
        {
            // A high rate makes the estimate swing far enough that a margin-based release would have let go
            var monitor = new ShuttleBatteryMonitor(Graph()) { KwhPerMeter = 0.01 };
            var raised = 0;
            monitor.ReturnRequired += _ => raised++;
            monitor.OnSnapshot(Snapshot("IN_SERVICE", 17.0, x: 900));   // threshold 17.25: ReturnNow
            monitor.OnSnapshot(Snapshot("IN_SERVICE", 16.8, x: 100));   // threshold 7.25, far above: still held
            Assert.AreEqual(BatteryRisk.ReturnNow, monitor.Latest["shuttle-01"].Risk);
            monitor.OnSnapshot(Snapshot("IN_SERVICE", 16.5, x: 900));   // away again
            Assert.AreEqual(1, raised, "no second warning without charging");

            monitor.OnSnapshot(Snapshot("IN_SERVICE", 30.0, x: 900));   // the battery rose: it charged
            Assert.AreEqual(BatteryRisk.Ok, monitor.Latest["shuttle-01"].Risk);
            monitor.OnSnapshot(Snapshot("IN_SERVICE", 17.0, x: 900));
            Assert.AreEqual(2, raised, "a new episode warns again");
        }

        // --- Closures the backend names by its own edge IDs ---

        [Test]
        public void ClosureNamedByTheBackend_IsFoundByItsPath()
        {
            var monitor = new ShuttleBatteryMonitor(Graph());
            var asked = new List<string>(); var messages = new List<string>();
            monitor.EdgePathsNeeded += ids => asked.AddRange(ids);
            monitor.Diagnostic += messages.Add;
            var snapshot = Snapshot("IN_SERVICE", 20, x: 1000, y: 500, heading: 90);
            snapshot.Disruptions = new List<Disruption> { Closure("edge-2") };   // the backend's name, not a road ID

            monitor.OnSnapshot(snapshot);
            CollectionAssert.AreEqual(new[] { "edge-2" }, asked, "the monitor asks for the edge's geometry");
            Assert.AreEqual(BatteryRisk.Ok, monitor.Latest["shuttle-01"].Risk, "nothing is closed until it arrives");

            monitor.SetEdgePaths(new Dictionary<string, Vector2[]> { { "edge-2", new[] { new Vector2(900, 900), new Vector2(100, 100) } } });   // along R3
            monitor.OnSnapshot(snapshot);
            Assert.AreEqual(BatteryRisk.Unknown, monitor.Latest["shuttle-01"].Risk, "R3 is closed: there is no way home");
            Assert.IsEmpty(messages);
            Assert.AreEqual(1, asked.Count, "asked once");
        }

        [Test]
        public void ClosuresThatMatchNothingByIdOrPosition_AreReportedOnce_NotIgnoredSilently()
        {
            var monitor = new ShuttleBatteryMonitor(Graph());
            var messages = new List<string>();
            monitor.Diagnostic += messages.Add;
            monitor.EdgePathsNeeded += _ => { };   // someone is listening, so the monitor waits for the answer
            var snapshot = Snapshot("IN_SERVICE", 20);
            snapshot.Disruptions = new List<Disruption> { Closure("edge-2") };
            monitor.OnSnapshot(snapshot);
            monitor.OnSnapshot(snapshot);
            Assert.IsEmpty(messages, "still waiting for the edge's geometry");

            monitor.SetEdgePaths(new Dictionary<string, Vector2[]>());   // there is none
            Assert.AreEqual(1, messages.Count);
            StringAssert.Contains("edge-2", messages[0]);
            monitor.OnSnapshot(snapshot);
            Assert.AreEqual(1, messages.Count);
        }

        [Test]
        public void ClosuresNobodyCanLookUp_AreStillReported()
        {
            var monitor = new ShuttleBatteryMonitor(Graph());   // nothing is listening for EdgePathsNeeded
            var messages = new List<string>();
            monitor.Diagnostic += messages.Add;
            var snapshot = Snapshot("IN_SERVICE", 20);
            snapshot.Disruptions = new List<Disruption> { Closure("return:11:forward") };
            monitor.OnSnapshot(snapshot);
            Assert.AreEqual(1, messages.Count);
            StringAssert.Contains("return:11:forward", messages[0]);
        }

        [Test]
        public void AFailedLookup_IsAskedAgainOnRetry_ButAnEmptyAnswerIsFinal()
        {
            var monitor = new ShuttleBatteryMonitor(Graph());
            var asked = new List<string>(); var messages = new List<string>();
            monitor.EdgePathsNeeded += ids => asked.AddRange(ids);
            monitor.Diagnostic += messages.Add;
            var snapshot = Snapshot("IN_SERVICE", 20);
            snapshot.Disruptions = new List<Disruption> { Closure("edge-2") };
            monitor.OnSnapshot(snapshot);
            Assert.AreEqual(1, asked.Count);

            monitor.SetEdgePaths(new Dictionary<string, Vector2[]>(), new[] { "edge-2" });   // the request failed
            Assert.IsEmpty(messages, "a failure says nothing about the roads");
            Assert.AreEqual(1, asked.Count, "not asked again at once");
            monitor.RetryClosures();
            Assert.AreEqual(2, asked.Count, "asked again on the retry");

            monitor.SetEdgePaths(new Dictionary<string, Vector2[]>());   // answered, with nothing: final
            monitor.RetryClosures();
            Assert.AreEqual(2, asked.Count);
        }

        [Test]
        public void ClosuresThatMatchARoad_AreNotReported()
        {
            var monitor = new ShuttleBatteryMonitor(Graph());
            var messages = new List<string>();
            monitor.Diagnostic += messages.Add;
            var snapshot = Snapshot("IN_SERVICE", 20);
            snapshot.Disruptions = new List<Disruption> { Closure("R3") };
            monitor.OnSnapshot(snapshot);
            Assert.IsEmpty(messages);
        }

        [Test]
        public void AtADepot_IsFlagged_AndOnTheRoadIsNot()
        {
            Assert.IsTrue(Graph().DistanceToDepot(new Vector2(20, 5)).AtDepot);
            Assert.IsFalse(Graph().DistanceToDepot(new Vector2(500, 0)).AtDepot);
        }

       [Test]
       public void AStandbyShuttleAwayFromADepot_IsStillWatched()
       {
            var monitor = new ShuttleBatteryMonitor(Graph());
            var raised = 0;
            monitor.ReturnRequired += _ => raised++;
            monitor.OnSnapshot(Snapshot("STANDBY", 6.0));   // 500 m out, at its reserve
            Assert.AreEqual(1, raised);
       }

       [Test]
       public void AShuttleAtADepot_IsNotWarned_WhateverItsState()
       {
            var monitor = new ShuttleBatteryMonitor(Graph());
            var raised = 0; var critical = 0;
            monitor.ReturnRequired += _ => raised++;
            monitor.Critical += _ => critical++;
            monitor.OnSnapshot(Snapshot("STANDBY", 1.0, x: 10));              // 10 m from the depot, nearly empty
            monitor.OnSnapshot(Snapshot("RETURNING_TO_DEPOT", 1.0, x: 10));
            Assert.AreEqual(0, raised + critical);
            Assert.IsFalse(monitor.Latest.ContainsKey("shuttle-01"));
        }

        [Test]
        public void AStrandedShuttle_IsNotWatched()
        {
            var monitor = new ShuttleBatteryMonitor(Graph());
            var raised = 0; var critical = 0;
            monitor.ReturnRequired += _ => raised++;
            monitor.Critical += _ => critical++;
            monitor.OnSnapshot(Snapshot("OUT_OF_SERVICE", 0.0));
            Assert.AreEqual(0, raised + critical);
            Assert.IsFalse(monitor.Latest.ContainsKey("shuttle-01"));
        }

        // 500 m out at 0.005 kWh/m: trip 2.5 kWh, threshold 2.5 * 1.25 + 6 = 9.125 kWh, above the gauge's red line (8 kWh of 40)
        [Test]
        public void RaisesOnce_WhenTheBatteryReachesTheThreshold()
        {
            var monitor = new ShuttleBatteryMonitor(Graph()) { KwhPerMeter = 0.005 };
            var raised = new List<BatteryAssessment>();
            monitor.ReturnRequired += raised.Add;

            monitor.OnSnapshot(Snapshot("IN_SERVICE", 9.5));
            Assert.AreEqual(0, raised.Count);
            monitor.OnSnapshot(Snapshot("IN_SERVICE", 9.0));
            monitor.OnSnapshot(Snapshot("IN_SERVICE", 8.9));
            Assert.AreEqual(1, raised.Count);
            Assert.AreEqual(BatteryRisk.ReturnNow, raised[0].Risk);
            Assert.IsFalse(raised[0].GaugeRed, "its own return point came first, not the red gauge");
        }

        // --- The gauge's red line (BatteryWatch.IsRed) ---

        [Test]
        public void ARedGauge_IsAlwaysReturnNow_SoTheMessageComesWithTheRedBar()
        {
            var monitor = new ShuttleBatteryMonitor(Graph());
            var raised = new List<BatteryAssessment>();
            monitor.ReturnRequired += raised.Add;
            monitor.OnSnapshot(Snapshot("IN_SERVICE", 8.5, x: 100));   // 21%, 100 m from the depot: nothing yet
            Assert.AreEqual(0, raised.Count);
            monitor.OnSnapshot(Snapshot("IN_SERVICE", 7.9, x: 100));   // 19.75%: the bar turns red; its own return point is only about 6 kWh
            Assert.AreEqual(1, raised.Count);
            Assert.IsTrue(raised[0].GaugeRed);
            Assert.Less(raised[0].ThresholdKwh, raised[0].BatteryKwh, "the return point alone would not have warned yet");
        }

        [Test]
        public void FarFromADepot_TheMessageComesBeforeTheGaugeTurnsRed()
        {
            var monitor = new ShuttleBatteryMonitor(Graph()) { KwhPerMeter = 0.005 };
            var raised = new List<BatteryAssessment>();
            monitor.ReturnRequired += raised.Add;
            monitor.OnSnapshot(Snapshot("IN_SERVICE", 11.0, x: 900));   // 27.5%: not red; return point 900 * 0.005 * 1.25 + 6 = 11.625
            Assert.AreEqual(1, raised.Count);
            Assert.IsFalse(raised[0].GaugeRed);
        }

        [Test]
        public void WithoutADistance_TheRedLineIsTheFallback()
        {
            var monitor = new ShuttleBatteryMonitor(Graph());
            var raised = 0;
            monitor.ReturnRequired += _ => raised++;
            monitor.OnSnapshot(Snapshot("IN_SERVICE", 8.5, depot: false));   // above the reserve, gauge not red
            Assert.AreEqual(BatteryRisk.Unknown, monitor.Latest["shuttle-01"].Risk);
            Assert.AreEqual(0, raised);
            monitor.OnSnapshot(Snapshot("IN_SERVICE", 7.9, depot: false));   // the gauge turns red
            Assert.AreEqual(1, raised);
        }

        [Test]
        public void ReturnMessage_SaysWhyItWarns()
        {
            var ownPoint = new BatteryAssessment { VehicleId = "shuttle-01", BatteryKwh = 9.0, CapacityKwh = 40, DistanceToDepotM = 900, ThresholdKwh = 11.6 };
            var redFirst = new BatteryAssessment { VehicleId = "shuttle-01", BatteryKwh = 7.9, CapacityKwh = 40, DistanceToDepotM = 100, ThresholdKwh = 6.1, GaugeRed = true };
            var unknown = new BatteryAssessment { VehicleId = "shuttle-01", BatteryKwh = 7.9, CapacityKwh = 40, DistanceToDepotM = double.NaN, ThresholdKwh = 6.0 };
            StringAssert.Contains("needs", ShuttleBatteryService.ReturnMessage(ownPoint));
            StringAssert.Contains("battery low", ShuttleBatteryService.ReturnMessage(redFirst));
            StringAssert.DoesNotContain("needs", ShuttleBatteryService.ReturnMessage(redFirst), "it already has more than its return point needs");
            StringAssert.DoesNotContain("NaN", ShuttleBatteryService.ReturnMessage(unknown));
        }
    }
}
