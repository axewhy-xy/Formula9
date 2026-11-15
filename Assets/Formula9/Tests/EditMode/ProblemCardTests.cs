using System.Collections.Generic;
using System.IO;
using System.Linq;
using Formula9.Contracts;
using Formula9.UI;
using NUnit.Framework;
using UnityEngine;

namespace Formula9.Tests
{
    // The Problem card on Route Planning, per shuttle, from the incident decisions fixture: at tick 200
    // (0.05 s a tick) a closure holds shuttle-01 (8 s, 40 m away), and a collision on Lorong 6 Toa Payoh
    // is reached by shuttle-03 (assistance already requested) and shuttle-04 (110 s, 1100 m away; may
    // keep its route, at a cost of 25 s, or hold at a stop; its options expire at tick 1400).
    public class ProblemCardTests
    {
        private const string Closure = "incident-1";
        private const string Collision = "scheduled-collision-1";

        private static FullSnapshot Snapshot(string status = "RUNNING")
        {
            var raw = File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/incident_decisions_snapshot.json"));
            var snapshot = (FullSnapshot)ContractParser.ParseMessage(raw);
            snapshot.Session.Status = status;
            return snapshot;
        }

        private static RouteEditorContext Roads(bool closed) => new RouteEditorContext
        {
            RouteId = "route-a",
            Nodes = new List<RouteEditorNode>(),
            Edges = new List<RouteEditorEdge> { new RouteEditorEdge { EdgeId = "edge-2", Closed = closed, RoadName = "Lorong 6 Toa Payoh" } },
            Stops = new List<RouteEditorStop> { new RouteEditorStop { StopId = "52449-a", Name = "Toa Payoh Police Centre" } },
            OriginalDirectedEdgeIds = new List<string> { "edge-2" },
            Assumptions = new List<string>(),
        };

        private static Incident Incident(FullSnapshot s, string id) => s.Incidents.Single(i => i.IncidentId == id);

        [Test]
        public void ACollisionSaysWhereWhenAndWhatKeepingTheRouteCosts()
        {
            var s = Snapshot();
            Assert.AreEqual(
                "COLLISION on Lorong 6 Toa Payoh.\nShuttle 04 reaches it in about 1 min 50 s (1.10 km).\nKeeping its route costs about 25 s.",
                RoutePresenter.Problem(null, s, "shuttle-04"), "an incident needs no road graph to be described");
            Assert.AreEqual("Options for 1 min", RoutePresenter.OptionsExpiry(s, "shuttle-04"), "1200 ticks at 0.05 s");
            CollectionAssert.AreEqual(new[] { Collision }, RoutePresenter.ProblemIncidents(s, "shuttle-04").Select(i => i.IncidentId));
        }

        [Test]
        public void ARunningClosureIsDescribedAsAnIncident()
        {
            var s = Snapshot();
            Assert.AreEqual("ROAD CLOSURE on Lorong 6 Toa Payoh.\nShuttle 01 reaches it in about 8 s (40 m).",
                RoutePresenter.Problem(Roads(true), s, "shuttle-01"));
            Assert.AreEqual("Options for 5 s", RoutePresenter.OptionsExpiry(s, "shuttle-01"));
        }

        [Test]
        public void BeforeTheStartTheClosureOnTheRouteIsTheProblem()
        {
            var s = Snapshot("READY");
            Assert.AreEqual("Lorong 6 Toa Payoh is closed. Shuttle 01 can't run its loop to Toa Payoh Police Centre. Find a way round it.",
                RoutePresenter.Problem(Roads(true), s, "shuttle-01"));
            CollectionAssert.IsEmpty(RoutePresenter.ProblemIncidents(s, "shuttle-01"));
            Assert.IsNull(RoutePresenter.OptionsExpiry(s, "shuttle-01"));
            Assert.AreEqual("No closure affects Shuttle 05's route.", RoutePresenter.Problem(Roads(false), s, "shuttle-05"));
        }

        [Test]
        public void ASignalFaultIsNamedForWhatItIs()
        {
            var s = Snapshot();
            Incident(s, Collision).EventType = "SIGNAL_FAULT";
            var text = RoutePresenter.Problem(Roads(false), s, "shuttle-04");
            StringAssert.StartsWith("SIGNAL FAULT on Lorong 6 Toa Payoh.", text);
            StringAssert.DoesNotContain("closure", text);
            StringAssert.Contains("Keeping its route costs about 25 s.", text);
        }

        [Test]
        public void TwoIncidentsAreListedSoonestFirst()
        {
            var s = Snapshot();
            var closure = Incident(s, Closure);
            closure.ExposedVehicleIds.Add("shuttle-04");
            closure.Exposures.Add(new IncidentExposure { VehicleId = "shuttle-04", EtaS = 30, DistanceM = 300, KeepRouteDelayS = 130 });
            var keep = new Candidate
            {
                CandidateId = "incident-1-r2-shuttle-04-keep_route", IncidentId = Closure, VehicleId = "shuttle-04", Action = "KEEP_ROUTE",
                Validation = "PASS", ExpiresAtTick = 260, Revision = 1, Assumptions = new List<string>(), DependencyRevisions = new Dictionary<string, long>(),
            };
            s.Candidates.Add(keep); closure.CandidateIds.Add(keep.CandidateId);

            CollectionAssert.AreEqual(new[] { Closure, Collision }, RoutePresenter.ProblemIncidents(s, "shuttle-04").Select(i => i.IncidentId));
            Assert.AreEqual(
                "Shuttle 04 has 2 incidents ahead, soonest first.\n\n" +
                "ROAD CLOSURE on Lorong 6 Toa Payoh.\nShuttle 04 reaches it in about 30 s (300 m).\nKeeping its route costs about 2 min 10 s.\n\n" +
                "COLLISION on Lorong 6 Toa Payoh.\nShuttle 04 reaches it in about 1 min 50 s (1.10 km).\nKeeping its route costs about 25 s.",
                RoutePresenter.Problem(null, s, "shuttle-04"));
            Assert.AreEqual("Options for 3 s", RoutePresenter.OptionsExpiry(s, "shuttle-04"), "the first incident's options, 60 ticks");
        }

        [Test]
        public void ADecidedShuttleSaysWhatWasDecided()
        {
            var s = Snapshot();
            Assert.AreEqual("COLLISION on Lorong 6 Toa Payoh.\nDecided for Shuttle 03: request assistance. Waiting to see how it turns out.",
                RoutePresenter.Problem(null, s, "shuttle-03"));
            Assert.IsNull(RoutePresenter.OptionsExpiry(s, "shuttle-03"));

            var response = Incident(s, Collision).ResponseOf("shuttle-03");
            response.Action = "HOLD_AT_STOP"; response.HoldStopId = "52449-kim-keat-stop"; response.BeforeArrival = false;
            StringAssert.EndsWith("Decided for Shuttle 03: hold at Kim Keat Stop, after it got there. Waiting to see how it turns out.",
                RoutePresenter.Problem(null, s, "shuttle-03"));

            // Once the shuttle has got through, there is nothing left to show for it
            response.CompletedTick = 199;
            CollectionAssert.IsEmpty(RoutePresenter.ProblemIncidents(s, "shuttle-03"));
        }

        [Test]
        public void AnIncidentWithNoOptionsAsksNothing()
        {
            var s = Snapshot();
            Incident(s, Collision).State = "DETECTED";
            Assert.AreEqual("COLLISION on Lorong 6 Toa Payoh.\nShuttle 04 reaches it in about 1 min 50 s (1.10 km).\nNo decision is asked for it now.",
                RoutePresenter.Problem(null, s, "shuttle-04"));
            Assert.IsNull(RoutePresenter.OptionsExpiry(s, "shuttle-04"));
        }

        [Test]
        public void AShuttleWithNothingAheadNeedsNoDecision()
        {
            var s = Snapshot();
            Assert.AreEqual("Nothing on Shuttle 05's route needs a decision now.", RoutePresenter.Problem(Roads(false), s, "shuttle-05"));
            Assert.AreEqual("Loading this route's roads…", RoutePresenter.Problem(null, s, "shuttle-05"));
        }
    }
}
