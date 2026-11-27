using System.Collections.Generic;
using System.IO;
using System.Linq;
using Formula9.Contracts;
using Formula9.Session;
using Formula9.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using Event = Formula9.Contracts.Event;

namespace Formula9.Tests
{
    // The decision card on Route Planning, from the incident decisions fixture: at tick 200 a closure
    // offers shuttle-01 a wait (an older backend's keep route), and a collision on Lorong 6 Toa Payoh,
    // reached by shuttle-03 (assistance already requested) and shuttle-04, offers shuttle-04 keep route
    // and hold at a stop, standing until tick 1400.
    public class DecisionOptionsTests
    {
        private const string Closure = "incident-1";
        private const string Collision = "scheduled-collision-1";
        private const string Keep = "scheduled-collision-1-r3-shuttle-04-keep_route";
        private const string Hold = "scheduled-collision-1-r3-shuttle-04-hold_at_stop";

        private static FullSnapshot Snapshot(string status = "RUNNING")
        {
            var raw = File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/incident_decisions_snapshot.json"));
            var snapshot = (FullSnapshot)ContractParser.ParseMessage(raw);
            snapshot.Session.Status = status;
            return snapshot;
        }

        private static Incident Incident(FullSnapshot s, string id) => s.Incidents.Single(i => i.IncidentId == id);

        // The backend's OPTIONS_READY evidence for the collision's options, as _vehicle_options writes it
        private static Event Ready(string incident = Collision, string keep = Keep, string hold = Hold) => new Event
        {
            EventType = "OPTIONS_READY", EntityType = "INCIDENT", EntityId = incident,
            Data = new JObject
            {
                ["options"] = new JObject
                {
                    [keep] = new JObject { ["action"] = "KEEP_ROUTE", ["estimated_delay_s"] = 25.0, ["hold_stop"] = null },
                    [hold] = new JObject { ["action"] = "HOLD_AT_STOP", ["estimated_delay_s"] = 118.5, ["hold_stop"] = new JObject { ["stop_id"] = "52449-kim-keat-stop", ["eta_s"] = 12.0 } },
                },
            },
        };

        private static Candidate Offer(FullSnapshot s, string incident, string vehicle, string action, string id = null)
        {
            var candidate = new Candidate
            {
                CandidateId = id ?? $"{incident}-{vehicle}-{action.ToLowerInvariant()}", IncidentId = incident, VehicleId = vehicle, Action = action,
                Validation = "PASS", ExpiresAtTick = 1400, Revision = 1, Assumptions = new List<string>(), DependencyRevisions = new Dictionary<string, long>(),
            };
            s.Candidates.Add(candidate); Incident(s, incident).CandidateIds.Add(candidate.CandidateId);
            return candidate;
        }

        private static DecisionOption Option(List<DecisionOption> options, string action) => options.Single(o => o.Action == action);

        [Test]
        public void TheButtonsAreTheShuttlesOptionsWithTheBackendsFigures()
        {
            var options = RoutePresenter.Options(Snapshot(), "shuttle-04", Ready());
            CollectionAssert.AreEqual(new[] { "KEEP_ROUTE", "HOLD_AT_STOP", "ESCALATE", "ROAD_DETOUR" }, options.Select(o => o.Action));
            Assert.IsTrue(options.All(o => o.Shown));

            var keep = Option(options, "KEEP_ROUTE");
            Assert.IsTrue(keep.Enabled);
            Assert.AreEqual("Keep route ~+25 s", keep.Label);
            Assert.AreEqual((Collision, "KEEP_ROUTE"), (keep.IncidentId, keep.CandidateAction), "bound to the incident and action, not an ID");
            Assert.AreEqual("Shuttle 04 keeps its route through the collision on Lorong 6 Toa Payoh, about 25 s later.", keep.Confirm);
            Assert.IsNotEmpty(keep.Reason, "the backend needs a reason, so one is offered");

            var hold = Option(options, "HOLD_AT_STOP");
            Assert.IsTrue(hold.Enabled);
            Assert.AreEqual("Hold at Kim Keat Stop ~+2 min", hold.Label);
            Assert.AreEqual("Hold Shuttle 04 at Kim Keat Stop until the collision on Lorong 6 Toa Payoh clears.", hold.Confirm);

            var assist = Option(options, "ESCALATE");
            Assert.IsFalse(assist.Enabled);
            Assert.AreEqual("Assistance has already been requested.", assist.Tooltip, "shuttle-03 asked for it");

            var reroute = Option(options, "ROAD_DETOUR");
            Assert.IsFalse(reroute.Enabled);
            Assert.AreEqual("Shuttle 04's route can't be taken round it.", reroute.Tooltip);
            Assert.AreEqual("What should Shuttle 04 do?", RoutePresenter.OptionsTitle("shuttle-04"));
        }

        [Test]
        public void WithoutTheEventTheButtonsShowNoFigures()
        {
            var s = Snapshot();
            foreach (var evidence in new[] { null, Ready(Closure), Ready(keep: "scheduled-collision-1-r2-shuttle-04-keep_route", hold: "scheduled-collision-1-r2-shuttle-04-hold_at_stop") })
            {
                var options = RoutePresenter.Options(s, "shuttle-04", evidence);
                // Keeping the route's cost is also in the snapshot, so it never goes missing
                Assert.AreEqual("Keep route ~+25 s", Option(options, "KEEP_ROUTE").Label, "no event, another incident's, or one for options since reissued");
                Assert.AreEqual("Hold at a stop", Option(options, "HOLD_AT_STOP").Label);
                Assert.AreEqual("Hold Shuttle 04 at the stop before it until the collision on Lorong 6 Toa Payoh clears.", Option(options, "HOLD_AT_STOP").Confirm);
                Assert.IsTrue(Option(options, "HOLD_AT_STOP").Enabled, "the decision itself comes from the snapshot");
            }
            // Without the snapshot's figure either, Keep route has none
            Incident(s, Collision).Exposures.Single(e => e.VehicleId == "shuttle-04").KeepRouteDelayS = null;
            Assert.AreEqual("Keep route", Option(RoutePresenter.Options(s, "shuttle-04", null), "KEEP_ROUTE").Label);
        }

        [Test]
        public void TheFiguresComeFromTheIncidentsLatestOptionsEvent()
        {
            var older = Ready(); older.EventSequence = 1;
            var other = Ready(Closure); other.EventSequence = 2;
            var latest = Ready(); latest.EventSequence = 3;
            var withdrawn = new Event { EventType = "OPTIONS_WITHDRAWN", EntityId = Collision, Data = new JObject(), EventSequence = 4 };
            Assert.AreSame(latest, RoutePresenter.OptionsEvent(new[] { older, other, latest, withdrawn }, Collision));
            Assert.IsNull(RoutePresenter.OptionsEvent(new[] { other }, Collision));
            Assert.IsNull(RoutePresenter.OptionsEvent(new[] { latest }, null));
        }

        [Test]
        public void ASignalFaultOffersNoReroute()
        {
            var s = Snapshot();
            Incident(s, Collision).EventType = "SIGNAL_FAULT";
            var reroute = Option(RoutePresenter.Options(s, "shuttle-04", null), "ROAD_DETOUR");
            Assert.IsFalse(reroute.Enabled);
            Assert.AreEqual("Signal faults can't be routed round.", reroute.Tooltip);
        }

        [Test]
        public void AssistanceSaysItIsForEveryShuttleThatReachesIt()
        {
            var s = Snapshot();
            Incident(s, Collision).Responses.Clear();
            Offer(s, Collision, "shuttle-04", "ESCALATE");
            var assist = Option(RoutePresenter.Options(s, "shuttle-04", null), "ESCALATE");
            Assert.IsTrue(assist.Enabled);
            Assert.AreEqual("Request assistance", assist.Label);
            Assert.AreEqual("Request assistance at the collision on Lorong 6 Toa Payoh. It applies to every shuttle that reaches it: Shuttle 03 and Shuttle 04.", assist.Confirm);

            // Not offered, and nobody asked: it just isn't available here
            s.Candidates.RemoveAll(c => c.Action == "ESCALATE");
            Assert.AreEqual("Assistance isn't offered for this incident.", Option(RoutePresenter.Options(s, "shuttle-04", null), "ESCALATE").Tooltip);
        }

        [Test]
        public void ReRouteAnswersTheLatestDetectedIncidentThatOffersADetour()
        {
            var s = Snapshot();
            Offer(s, Collision, "shuttle-04", "ROAD_DETOUR");
            var reroute = Option(RoutePresenter.Options(s, "shuttle-04", null), "ROAD_DETOUR");
            Assert.IsTrue(reroute.Enabled);
            Assert.AreEqual("Re-route on the map", reroute.Label);
            Assert.AreEqual((Collision, "ROAD_DETOUR"), (reroute.IncidentId, reroute.CandidateAction));
            Assert.AreEqual("Draw a way round it on the map.", reroute.Tooltip);
            Assert.AreEqual("Go round the collision on Lorong 6 Toa Payoh", reroute.Reason);
            Assert.IsNull(reroute.Confirm, "a re-route is confirmed by approving its route");

            // The closure, detected earlier, is sooner: the card decides it, but a route answers the collision
            var closure = Incident(s, Closure);
            closure.ExposedVehicleIds.Add("shuttle-04");
            closure.Exposures.Add(new IncidentExposure { VehicleId = "shuttle-04", EtaS = 30, DistanceM = 300 });
            Offer(s, Closure, "shuttle-04", "KEEP_ROUTE");
            Offer(s, Closure, "shuttle-04", "ROAD_DETOUR");
            var options = RoutePresenter.Options(s, "shuttle-04", null);
            Assert.AreEqual(Closure, Option(options, "KEEP_ROUTE").IncidentId, "the first incident is decided");
            reroute = Option(options, "ROAD_DETOUR");
            Assert.AreEqual(Collision, reroute.IncidentId, "as route_edit.detour_target picks it");
            StringAssert.Contains("the collision on Lorong 6 Toa Payoh", reroute.Tooltip);
            StringAssert.Contains("latest one detected", reroute.Tooltip);
        }

        [Test]
        public void AnOlderBackendsWaitIsKeepRoute()
        {
            var s = Snapshot();
            var options = RoutePresenter.Options(s, "shuttle-01", null);
            var keep = Option(options, "KEEP_ROUTE");
            Assert.IsTrue(keep.Enabled);
            Assert.AreEqual((Closure, "WAIT"), (keep.IncidentId, keep.CandidateAction), "sent as the option the backend offered");
            Assert.AreEqual("There is no stop before it to hold at.", Option(options, "HOLD_AT_STOP").Tooltip);

            s.Vehicles.Single(v => v.VehicleId == "shuttle-01").ServiceState = "AWAITING_GUIDANCE";
            Assert.AreEqual("Shuttle 01 is already stopped at it.", Option(RoutePresenter.Options(s, "shuttle-01", null), "HOLD_AT_STOP").Tooltip);
        }

        [Test]
        public void PausedOrExpiredOptionsCantBeChosen()
        {
            var paused = RoutePresenter.Options(Snapshot("PAUSED"), "shuttle-04", Ready());
            Assert.IsTrue(paused.All(o => o.Shown), "still listed while paused");
            Assert.IsFalse(paused.Any(o => o.Enabled));
            Assert.AreEqual("The session is paused. Resume it to decide.", Option(paused, "KEEP_ROUTE").Tooltip);

            var s = Snapshot(); s.Tick = 1401;
            var keep = Option(RoutePresenter.Options(s, "shuttle-04", null), "KEEP_ROUTE");
            Assert.IsFalse(keep.Enabled);
            Assert.AreEqual("Options are being refreshed.", keep.Tooltip);

            s = Snapshot(); s.Candidates.Single(c => c.CandidateId == Keep).Validation = "FAIL";
            Assert.IsFalse(Option(RoutePresenter.Options(s, "shuttle-04", null), "KEEP_ROUTE").Enabled);
        }

        [Test]
        public void TheHintSaysWhyTheButtonsCantBeUsed()
        {
            Assert.AreEqual("The session is paused. Resume it to decide.", RoutePresenter.OptionsHint(Snapshot("PAUSED"), true, false, true));
            StringAssert.StartsWith("Not connected", RoutePresenter.OptionsHint(Snapshot(), false, false, true));
            Assert.AreEqual("Sending a decision…", RoutePresenter.OptionsHint(Snapshot(), true, true, true));
            StringAssert.StartsWith("Waiting for a fresh update", RoutePresenter.OptionsHint(Snapshot(), true, false, false));
            Assert.IsNull(RoutePresenter.OptionsHint(Snapshot(), true, false, true));
        }

        [Test]
        public void AShuttleWithNoDecisionHasNoButtons()
        {
            Assert.IsFalse(RoutePresenter.Options(Snapshot(), "shuttle-05", null).Any(o => o.Shown || o.Enabled));
            Assert.IsFalse(RoutePresenter.Options(Snapshot(), "shuttle-03", null).Any(o => o.Shown), "already decided for");
            Assert.IsFalse(RoutePresenter.Options(Snapshot("READY"), "shuttle-04", null).Any(o => o.Shown), "nothing is decided before the start");
        }

        [Test]
        public void ARefusalSaysWhy()
        {
            Assert.AreEqual("The backend refused it (STALE_REVISION).", RoutePresenter.Refusal(new CommandOutcome { Status = "REJECTED", FailureCode = "STALE_REVISION" }));
            StringAssert.StartsWith("The option expired", RoutePresenter.Refusal(new CommandOutcome { Status = "EXPIRED" }));
            StringAssert.Contains("isn't known whether this was applied", RoutePresenter.Refusal(new CommandOutcome { Status = "NEEDS_RECONFIRMATION" }));
        }
    }
}
