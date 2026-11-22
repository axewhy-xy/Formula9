using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Formula9.Contracts;
using Formula9.Session;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Formula9.Tests
{
    // Decisions per shuttle, from the incident decisions fixture: a closure holding shuttle-01 (arrives in
    // 8 s), and a collision shuttle-03 (85 s, assistance already requested) and shuttle-04 (110 s, may keep
    // its route or hold at a stop) will reach. Each shuttle is given its own route, as in the integrated
    // fixture, so a detour for one never opens another's route.
    public class DecisionRulesTests
    {
        private const string Closure = "incident-1";
        private const string Collision = "scheduled-collision-1";

        private static string Fixture(string name) => File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures", name + ".json"));

        private static FullSnapshot Snapshot()
        {
            var raw = JObject.Parse(Fixture("incident_decisions_snapshot"));
            var route = (JObject)raw["routes"][0];
            raw["routes"] = new JArray(raw["vehicles"].Select(v =>
            {
                var own = (JObject)route.DeepClone(); own["route_id"] = RouteOf((string)v["vehicle_id"]); return own;
            }));
            foreach (var vehicle in raw["vehicles"]) vehicle["route_id"] = RouteOf((string)vehicle["vehicle_id"]);
            return (FullSnapshot)ContractParser.ParseMessage(raw.ToString());
        }

        private static string RouteOf(string vehicleId) => "route-" + vehicleId;

        // An option as the backend names it, offered to one shuttle.
        private static Candidate Offer(FullSnapshot s, string incidentId, string vehicleId, string action)
        {
            var incident = s.Incidents.Single(i => i.IncidentId == incidentId);
            var candidate = new Candidate
            {
                CandidateId = $"{incidentId}-r{incident.AggregateRevision}-{vehicleId}-{action.ToLowerInvariant()}",
                IncidentId = incidentId, VehicleId = vehicleId, Action = action, Validation = "PASS",
                ExpiresAtTick = s.Tick + 100, Revision = 1, Assumptions = new List<string>(), DependencyRevisions = new Dictionary<string, long>(),
            };
            s.Candidates.Add(candidate); incident.CandidateIds.Add(candidate.CandidateId);
            return candidate;
        }

        // A fresh, approvable draft of this shuttle's route.
        private static RouteEditPreview DraftFor(FullSnapshot s, string vehicleId)
        {
            var preview = ContractParser.Parse<RouteEditPreview>(Fixture("route_edit_preview"));
            var route = s.Routes.Single(r => r.RouteId == RouteOf(vehicleId));
            preview.RouteId = route.RouteId; preview.RouteRevision = route.Revision;
            preview.SessionId = s.SessionId; preview.SessionEpoch = s.SessionEpoch; preview.SceneRevision = s.SceneRevision;
            preview.ClosureRevisions = s.Disruptions.ToDictionary(d => d.DisruptionId, d => d.AggregateRevision);
            return preview;
        }

        private static string[] Pending(FullSnapshot s) => DecisionRules.PendingDecisions(s).Select(d => $"{d.IncidentId}/{d.VehicleId}").ToArray();

        [Test]
        public void EveryShuttleWithOptionsIsPendingSoonestArrivalFirst()
        {
            var s = Snapshot();
            // shuttle-03 has had its decision, so it has no options left
            CollectionAssert.AreEqual(new[] { $"{Closure}/shuttle-01", $"{Collision}/shuttle-04" }, Pending(s));
            var hold = DecisionRules.OptionsFor(s, "shuttle-04").Single();
            Assert.AreEqual(110, hold.EtaS);
            Assert.AreEqual(1100, hold.DistanceM);
            Assert.AreEqual(25, hold.KeepRouteDelayS);
            CollectionAssert.AreEquivalent(new[] { "KEEP_ROUTE", "HOLD_AT_STOP" }, hold.Candidates.Keys);
            Assert.AreEqual($"{Collision}-r3-shuttle-04-hold_at_stop", hold.Option("HOLD_AT_STOP").CandidateId);
            Assert.IsNull(hold.Option("ROAD_DETOUR"));
            CollectionAssert.IsEmpty(DecisionRules.OptionsFor(s, "shuttle-03"));
            CollectionAssert.IsEmpty(DecisionRules.OptionsFor(s, "shuttle-05"));
        }

        [Test]
        public void TwoShuttlesAtOneIncidentEachGetTheirOwnDetourAndCommand()
        {
            var s = Snapshot();
            s.Incidents.Single(i => i.IncidentId == Collision).Responses.Clear();
            Offer(s, Collision, "shuttle-03", "KEEP_ROUTE");
            var detours = new Dictionary<string, Candidate>
            {
                ["shuttle-03"] = Offer(s, Collision, "shuttle-03", "ROAD_DETOUR"),
                ["shuttle-04"] = Offer(s, Collision, "shuttle-04", "ROAD_DETOUR"),
            };
            CollectionAssert.AreEqual(new[] { $"{Closure}/shuttle-01", $"{Collision}/shuttle-03", $"{Collision}/shuttle-04" }, Pending(s));

            foreach (var (vehicle, detour) in detours)
            {
                Assert.AreSame(detour, DecisionRules.DetourCandidate(s, vehicle));
                Assert.AreEqual(RouteOf(vehicle), DecisionRules.ReroutableRoute(s, vehicle));
                var draft = DraftFor(s, vehicle);
                Assert.IsTrue(DecisionRules.RoutePreviewMatches(s, vehicle, draft));
                var command = DecisionRules.RerouteCommand(s, vehicle, draft, "Round the collision");
                Assert.AreEqual(Collision, command.TargetId);
                Assert.AreEqual(3, command.ExpectedRevision);
                Assert.AreEqual(detour.CandidateId, (string)command.Payload["candidate_id"]);
                ContractParser.Serialize(command);
            }

            // A preview must answer the live closures exactly: one listing a closure the session doesn't
            // have (the planner's own, round an incident's scene) is stale, and Approve stays disabled
            var planned = DraftFor(s, "shuttle-03");
            planned.ClosureRevisions[Collision + "-scene"] = 1;
            Assert.IsFalse(DecisionRules.RoutePreviewMatches(s, "shuttle-03", planned));
            Assert.IsTrue(DecisionRules.RoutePreviewMatches(s, "shuttle-03", DraftFor(s, "shuttle-03")));

            // One shuttle's draft never answers for another, nor for a shuttle with no detour on offer
            var drafted03 = DraftFor(s, "shuttle-03");
            Assert.IsFalse(DecisionRules.RoutePreviewMatches(s, "shuttle-04", drafted03));
            Assert.Throws<ArgumentException>(() => DecisionRules.RerouteCommand(s, "shuttle-04", drafted03, "Wrong shuttle"));
            Assert.IsNull(DecisionRules.DetourCandidate(s, "shuttle-01"));
            Assert.IsNull(DecisionRules.ReroutableRoute(s, "shuttle-05"));
            Assert.IsNull(DecisionRules.ReroutableRoute(s, null));
        }

        [Test]
        public void OneShuttleAtTwoIncidentsDetoursRoundTheLatestDetected()
        {
            var s = Snapshot();
            var collision = s.Incidents.Single(i => i.IncidentId == Collision);
            // Detected after the collision, but shuttle-04 reaches it later
            s.Incidents.Add(new Incident
            {
                IncidentId = "breakdown-1", EventType = "BREAKDOWN", State = "OPTIONS_READY", AggregateRevision = 1,
                DetectedTick = collision.DetectedTick + 5, CandidateIds = new List<string>(), Provenance = collision.Provenance,
                ExposedVehicleIds = new List<string> { "shuttle-04" },
                Exposures = new List<IncidentExposure> { new IncidentExposure { VehicleId = "shuttle-04", EtaS = 300, DistanceM = 2500 } },
            });
            Offer(s, "breakdown-1", "shuttle-04", "KEEP_ROUTE");
            var later = Offer(s, "breakdown-1", "shuttle-04", "ROAD_DETOUR");
            var sooner = Offer(s, Collision, "shuttle-04", "ROAD_DETOUR");

            // The Problem card lists them in arrival order...
            CollectionAssert.AreEqual(new[] { Collision, "breakdown-1" }, DecisionRules.OptionsFor(s, "shuttle-04").Select(d => d.IncidentId));
            // ...but a drafted detour answers the latest-detected one, as the backend's detour_target checks it
            Assert.AreSame(later, DecisionRules.DetourCandidate(s, "shuttle-04"));
            var command = DecisionRules.RerouteCommand(s, "shuttle-04", DraftFor(s, "shuttle-04"), "Round the breakdown");
            Assert.AreEqual("breakdown-1", command.TargetId);
            Assert.AreEqual(later.CandidateId, (string)command.Payload["candidate_id"]);

            // Once that one is decided, the next detour on offer is the collision's
            s.Incidents.Single(i => i.IncidentId == "breakdown-1").State = "EXECUTING";
            Assert.AreSame(sooner, DecisionRules.DetourCandidate(s, "shuttle-04"));
        }

        [Test]
        public void AReissuedOptionIsFoundByIncidentShuttleAndAction()
        {
            var s = Snapshot();
            var chosen = DecisionRules.CandidateFor(s, Collision, "shuttle-04", "HOLD_AT_STOP");
            Assert.AreEqual($"{Collision}-r3-shuttle-04-hold_at_stop", chosen.CandidateId);

            // A new revision reissues every option under a new ID
            var collision = s.Incidents.Single(i => i.IncidentId == Collision);
            collision.AggregateRevision = 4; collision.CandidateIds.Clear();
            s.Candidates.RemoveAll(c => c.IncidentId == Collision);
            Offer(s, Collision, "shuttle-04", "KEEP_ROUTE");
            var reissued = Offer(s, Collision, "shuttle-04", "HOLD_AT_STOP");

            Assert.Throws<ArgumentException>(() => DecisionRules.IncidentCommand(s, Collision, chosen.CandidateId, "Hold before the queue"));
            var current = DecisionRules.CandidateFor(s, Collision, "shuttle-04", "HOLD_AT_STOP");
            Assert.AreSame(reissued, current);
            var command = DecisionRules.IncidentCommand(s, Collision, current.CandidateId, "Hold before the queue");
            Assert.AreEqual(4, command.ExpectedRevision);
            Assert.AreEqual($"{Collision}-r4-shuttle-04-hold_at_stop", (string)command.Payload["candidate_id"]);

            Assert.IsNull(DecisionRules.CandidateFor(s, Collision, "shuttle-03", "HOLD_AT_STOP"));
            Assert.IsNull(DecisionRules.CandidateFor(s, Collision, "shuttle-04", "ROAD_DETOUR"));
            Assert.IsNull(DecisionRules.CandidateFor(s, "no-such-incident", "shuttle-04", "HOLD_AT_STOP"));
        }

        [Test]
        public void AnOptionNamingNoShuttleIsForTheIncidentsOwn()
        {
            var s = Snapshot();
            var wait = s.Candidates.Single(c => c.IncidentId == Closure);
            wait.VehicleId = null;  // as a snapshot from before each shuttle had its own options
            Assert.AreSame(wait, DecisionRules.CandidateFor(s, Closure, "shuttle-01", "WAIT"));
            Assert.AreEqual("shuttle-01", DecisionRules.PendingDecisions(s).First().VehicleId);
            var detour = Offer(s, Closure, null, "ROAD_DETOUR");
            Assert.AreSame(detour, DecisionRules.DetourCandidate(s, "shuttle-01"));
            Assert.AreEqual(RouteOf("shuttle-01"), DecisionRules.ReroutableRoute(s, "shuttle-01"));
        }

        [Test]
        public void ASignalFaultOffersNoDetour()
        {
            var s = Snapshot();
            // The backend offers a shuttle at a signal fault keep, hold or assistance, never a detour
            s.Incidents.Single(i => i.IncidentId == Collision).EventType = "SIGNAL_FAULT";
            var pending = DecisionRules.OptionsFor(s, "shuttle-04").Single();
            Assert.IsNull(pending.Option("ROAD_DETOUR"));
            Assert.IsNull(DecisionRules.DetourCandidate(s, "shuttle-04"));
            Assert.IsNull(DecisionRules.ReroutableRoute(s, "shuttle-04"));
            Assert.IsFalse(DecisionRules.RoutePreviewMatches(s, "shuttle-04", DraftFor(s, "shuttle-04")));
            Assert.Throws<ArgumentException>(() => DecisionRules.RerouteCommand(s, "shuttle-04", DraftFor(s, "shuttle-04"), "Round the junction"));
            Assert.DoesNotThrow(() => DecisionRules.IncidentCommand(s, Collision, pending.Option("HOLD_AT_STOP").CandidateId, "Hold until the signal is fixed"));
        }

        [Test]
        public void PausedKeepsDecisionsListedButBlocksCommands()
        {
            var s = Snapshot();
            var detour = Offer(s, Collision, "shuttle-04", "ROAD_DETOUR");
            var draft = DraftFor(s, "shuttle-04");
            var hold = DecisionRules.CandidateFor(s, Collision, "shuttle-04", "HOLD_AT_STOP");
            s.Session.Status = "PAUSED";

            CollectionAssert.AreEqual(new[] { $"{Closure}/shuttle-01", $"{Collision}/shuttle-04" }, Pending(s));
            Assert.AreSame(detour, DecisionRules.DetourCandidate(s, "shuttle-04"));  // the draft stays
            Assert.IsNull(DecisionRules.ReroutableRoute(s, "shuttle-04"));
            Assert.IsFalse(DecisionRules.RoutePreviewMatches(s, "shuttle-04", draft));
            Assert.Throws<ArgumentException>(() => DecisionRules.RerouteCommand(s, "shuttle-04", draft, "Round the collision"));
            Assert.Throws<ArgumentException>(() => DecisionRules.IncidentCommand(s, Collision, hold.CandidateId, "Hold before the queue"));

            s.Session.Status = "RUNNING";
            Assert.DoesNotThrow(() => DecisionRules.RerouteCommand(s, "shuttle-04", draft, "Round the collision"));

            // Nothing is pending before the start or after the end
            s.Session.Status = "COMPLETED";
            CollectionAssert.IsEmpty(DecisionRules.PendingDecisions(s));
            Assert.IsNull(DecisionRules.DetourCandidate(s, "shuttle-04"));
        }
    }
}
