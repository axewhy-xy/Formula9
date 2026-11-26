using System.Collections.Generic;
using System.IO;
using System.Linq;
using Formula9.Audio;
using Formula9.Contracts;
using Formula9.Session;
using Formula9.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using Event = Formula9.Contracts.Event;

namespace Formula9.Tests
{
    // The incident notifications (Notifications.cs), from the incident decisions fixture at event
    // sequence 7: a closure (incident-1) on shuttle-01's route, and a collision on Lorong 6 Toa Payoh
    // reached by shuttle-03, already decided for (assistance), and shuttle-04, still to be decided for.
    // The tests set the collision's ETAs to 40 s for shuttle-04 and 2 min for shuttle-03.
    public class NotificationTests
    {
        private const string Closure = "incident-1";
        private const string Collision = "scheduled-collision-1";

        private static FullSnapshot Snapshot(long sequence = 7)
        {
            var raw = File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures/incident_decisions_snapshot.json"));
            var snapshot = (FullSnapshot)ContractParser.ParseMessage(raw);
            snapshot.EventSequence = sequence;
            Exposure(snapshot, "shuttle-04").EtaS = 40;
            Exposure(snapshot, "shuttle-03").EtaS = 120;
            return snapshot;
        }

        private static Incident Incident(FullSnapshot s, string id) => s.Incidents.Single(i => i.IncidentId == id);
        private static IncidentExposure Exposure(FullSnapshot s, string vehicle) => Incident(s, Collision).Exposures.Single(e => e.VehicleId == vehicle);

        private static void Expose(FullSnapshot s, string vehicle, double eta)
        {
            var incident = Incident(s, Collision);
            incident.Exposures.Add(new IncidentExposure { VehicleId = vehicle, EtaS = eta, DistanceM = eta * 10 });
            incident.ExposedVehicleIds.Add(vehicle);
        }

        private static void Decide(FullSnapshot s, string vehicle, string action) =>
            (Incident(s, Collision).Responses ?? (Incident(s, Collision).Responses = new List<IncidentResponse>()))
            .Add(new IncidentResponse { VehicleId = vehicle, Action = action, BeforeArrival = action != "NO_DECISION", DecidedTick = 199 });

        private static int ids;

        private static Event E(string type, JObject data, long sequence = 5, string incident = Collision) => new Event
        {
            EventId = "event-" + ++ids, EntityType = "INCIDENT", EntityId = incident, EventType = type, EventSequence = sequence, Data = data,
        };

        // A collision detected with these shuttles reaching it, as the backend's _world() describes it
        private static Event Detected(long sequence = 5, bool alert = true, params (string Vehicle, double Eta)[] reach) => E("INCIDENT_DETECTED", new JObject
        {
            ["alert"] = alert, ["event_type"] = "COLLISION", ["incident_type"] = "ROAD_ACCIDENT",
            ["location"] = new JObject { ["edge_id"] = "edge-1", ["road_name"] = "Lorong 6 Toa Payoh" },
            ["exposures"] = new JArray(reach.Select(r => new JObject { ["vehicle_id"] = r.Vehicle, ["eta_s"] = r.Eta, ["distance_m"] = r.Eta * 10 })),
        }, sequence);

        private static Event Ready(bool alert, params string[] added) => E("OPTIONS_READY", new JObject
        {
            ["alert"] = alert, ["added_vehicle_ids"] = new JArray(added), ["vehicle_ids"] = new JArray("shuttle-04"),
        });

        private static List<string> Open(NotificationQueue queue) => queue.Items.Select(n => n.VehicleId).ToList();

        [Test]
        public void ADetectedIncidentNotifiesEachShuttleStillToBeDecidedFor()
        {
            var queue = new NotificationQueue();
            queue.OnSnapshot(Snapshot());
            Assert.IsTrue(queue.OnEvent(Detected(reach: new[] { ("shuttle-03", 120.0), ("shuttle-04", 40.0) })));

            CollectionAssert.AreEqual(new[] { "shuttle-04" }, Open(queue), "shuttle-03 was already decided for");
            var n = queue.Items[0];
            Assert.AreEqual((Collision, NotificationKind.Detected), (n.IncidentId, n.Kind));
            Assert.AreEqual("New incident", n.Tag);
            Assert.AreEqual("Collision · Lorong 6 Toa Payoh", n.Title);
            Assert.AreEqual("Shuttle 04 reaches it in about 40 s. Shuttle 03 in about 2 min.", n.Body);
            Assert.AreEqual("Decide for Shuttle 04", n.Decide);
            Assert.AreEqual(40, n.EtaS);
        }

        [Test]
        public void TheTextFollowsTheSnapshotAndCountsDown()
        {
            var queue = new NotificationQueue();
            queue.OnSnapshot(Snapshot());
            queue.OnEvent(Detected(reach: ("shuttle-04", 40.0)));
            var later = Snapshot(8);
            Exposure(later, "shuttle-04").EtaS = 0.2;
            var changed = 0;
            queue.Changed += () => changed++;
            queue.OnSnapshot(later);
            Assert.AreEqual(1, changed);
            StringAssert.StartsWith("Shuttle 04 reaches it now.", queue.Items[0].Body);
        }

        // The card's last line says what the 3D view's status box used to: the event's impact, how long
        // until the incident clears (counted from the snapshot's ticks), and the shuttle it is slowing,
        // which only the 3D city knows. With none of them known there is no line.
        [Test]
        public void TheDetailLineSaysTheImpactWhenItClearsAndWhoItSlows()
        {
            var s = Snapshot();
            s.Tick = 200; s.SimulationTimeS = 10; // 0.05 s a tick
            Incident(s, Collision).Location = null; // no closure on its road to end sooner
            Incident(s, Collision).ClearsAtTick = 700; // 500 ticks, 25 s
            var queue = new NotificationQueue { SlowedBy = id => id == Collision ? new FleetVehicle { VehicleId = "shuttle-02", SpeedMps = 3.1 } : null };
            queue.OnSnapshot(s);
            var detected = Detected(reach: ("shuttle-04", 40.0));
            detected.Data["operational_impact"] = "Lane blocked";
            queue.OnEvent(detected);
            Assert.AreEqual("Lane blocked · clears in 25 s · Shuttle 02 slowed to 3.1 m/s", queue.Items[0].Detail);

            var plain = Snapshot();
            Incident(plain, Collision).Location = null;
            Incident(plain, Collision).ClearsAtTick = null;
            var quiet = new NotificationQueue();
            quiet.OnSnapshot(plain);
            quiet.OnEvent(Detected(reach: ("shuttle-04", 40.0)));
            Assert.IsNull(quiet.Items[0].Detail);
        }

        [Test]
        public void BeforeTheSnapshotListsTheIncidentItClearsWhenTheEventSaid()
        {
            var s = Snapshot();
            s.Tick = 200; s.SimulationTimeS = 10; // 0.05 s a tick
            var n = new IncidentNotification { Impact = "Lane blocked", ActiveUntilTick = 700 };
            Assert.AreEqual("Lane blocked · clears in 25 s", NotificationQueue.Detail(n, null, s, null));
            Assert.AreEqual("Lane blocked", NotificationQueue.Detail(n, null, null, null), "no snapshot, no tick to count from");
        }

        [Test]
        public void OnceTheClearingTickHasPassedTheLineNoLongerSaysWhenItClears()
        {
            var s = Snapshot();
            s.Tick = 700; s.SimulationTimeS = 35; // 0.05 s a tick
            var n = new IncidentNotification { Impact = "Lane blocked", ActiveUntilTick = 700 };
            Assert.AreEqual("Lane blocked", NotificationQueue.Detail(n, null, s, null), "not \"clears in 0 s\"");
            s.Tick = 699;
            Assert.AreEqual("Lane blocked · clears in 1 s", NotificationQueue.Detail(n, null, s, null));
        }

        [Test]
        public void BeforeAnySnapshotTheTextComesFromTheEvent()
        {
            var queue = new NotificationQueue();
            queue.OnEvent(Detected(reach: new[] { ("shuttle-05", 75.0), ("shuttle-02", 30.0) }));
            CollectionAssert.AreEqual(new[] { "shuttle-02", "shuttle-05" }, Open(queue), "soonest first");
            Assert.AreEqual("Collision · Lorong 6 Toa Payoh", queue.Items[0].Title);
            Assert.AreEqual("Shuttle 02 reaches it in about 30 s. Shuttle 05 in about 1 min 20 s.", queue.Items[0].Body);
        }

        [Test]
        public void OnlyAlertsAndAddedShuttlesNotify()
        {
            var queue = new NotificationQueue();
            queue.OnSnapshot(Snapshot());
            Assert.IsFalse(queue.OnEvent(Detected(alert: false, reach: ("shuttle-04", 40.0))), "an incident no shuttle reaches");
            Assert.IsFalse(queue.OnEvent(Ready(false, "shuttle-04")), "options issued with the detection, which already alerted");
            Assert.IsFalse(queue.OnEvent(E("INCIDENT_EXPOSURE_CHANGED", new JObject { ["added_vehicle_ids"] = new JArray(), ["removed_vehicle_ids"] = new JArray("shuttle-04") })));
            Assert.IsFalse(queue.OnEvent(E("COMMAND_APPLIED", new JObject())));
            Assert.IsFalse(queue.OnEvent(new Event { EntityType = "VEHICLE", EventType = "OPTIONS_READY", EntityId = "shuttle-04", Data = new JObject { ["alert"] = true } }));
            Assert.IsEmpty(queue.Items);
        }

        [Test]
        public void AShuttleNewlyReachingItIsNotified()
        {
            var queue = new NotificationQueue();
            var s = Snapshot();
            Expose(s, "shuttle-05", 30);
            queue.OnSnapshot(s);
            queue.OnEvent(Detected(reach: ("shuttle-04", 40.0)));
            Assert.IsTrue(queue.OnEvent(E("INCIDENT_EXPOSURE_CHANGED", new JObject { ["added_vehicle_ids"] = new JArray("shuttle-05") }, 6)));

            CollectionAssert.AreEqual(new[] { "shuttle-05", "shuttle-04" }, Open(queue), "soonest arrival first");
            var n = queue.Items[0];
            Assert.AreEqual(NotificationKind.AlsoReaches, n.Kind);
            Assert.AreEqual("Another shuttle will reach it", n.Tag);
            Assert.AreEqual("Shuttle 05 will also reach it in about 30 s.", n.Body);
            Assert.AreEqual("Decide for Shuttle 05", n.Decide);
        }

        [Test]
        public void OptionsReadyNotifiesTheShuttlesNewlyOfferedThem()
        {
            var queue = new NotificationQueue();
            queue.OnSnapshot(Snapshot());
            Assert.IsTrue(queue.OnEvent(Ready(true, "shuttle-04")));
            var n = queue.Items.Single();
            Assert.AreEqual(("shuttle-04", NotificationKind.OptionsReady, "Options ready"), (n.VehicleId, n.Kind, n.Tag));
            Assert.AreEqual("Options are ready for Shuttle 04, which reaches it in about 40 s.", n.Body);
        }

        [Test]
        public void OneNotificationPerShuttleAndIncident()
        {
            var queue = new NotificationQueue();
            queue.OnSnapshot(Snapshot());
            queue.OnEvent(Detected(reach: ("shuttle-04", 40.0)));
            queue.OnEvent(Ready(true, "shuttle-04"));
            Assert.AreEqual(1, queue.Items.Count);
            Assert.AreEqual(NotificationKind.OptionsReady, queue.Items[0].Kind, "the latest news replaces the earlier");
        }

        [Test]
        public void WithdrawnOptionsCloseTheIncidentsNotifications()
        {
            var queue = new NotificationQueue();
            queue.OnEvent(Detected(reach: new[] { ("shuttle-04", 40.0), ("shuttle-05", 60.0) }));
            queue.OnEvent(E("INCIDENT_DETECTED", new JObject { ["alert"] = true, ["exposures"] = new JArray(new JObject { ["vehicle_id"] = "shuttle-01", ["eta_s"] = 8.0 }) }, 5, Closure));
            Assert.AreEqual(3, queue.Items.Count);

            Assert.IsTrue(queue.OnEvent(E("OPTIONS_WITHDRAWN", new JObject { ["vehicle_ids"] = new JArray("shuttle-04", "shuttle-05") }, 8)));
            CollectionAssert.AreEqual(new[] { "shuttle-01" }, Open(queue), "the closure's stays");
        }

        [TestCase("KEEP_ROUTE")]
        [TestCase("HOLD_AT_STOP")]
        [TestCase("NO_DECISION")]
        public void ADecidedOrMissedShuttleIsNoLongerNotified(string action)
        {
            var queue = new NotificationQueue();
            queue.OnSnapshot(Snapshot());
            queue.OnEvent(Detected(reach: ("shuttle-04", 40.0)));
            var decided = Snapshot(8);
            Decide(decided, "shuttle-04", action);
            queue.OnSnapshot(decided);
            Assert.IsEmpty(queue.Items);
        }

        [TestCase("RESOLVED")]
        [TestCase("UNRESOLVED")]
        public void AnIncidentThatEndedIsNoLongerNotified(string state)
        {
            var queue = new NotificationQueue();
            queue.OnSnapshot(Snapshot());
            queue.OnEvent(Detected(reach: ("shuttle-04", 40.0)));
            var ended = Snapshot(8);
            Incident(ended, Collision).State = state;
            queue.OnSnapshot(ended);
            Assert.IsEmpty(queue.Items);
        }

        [Test]
        public void ASnapshotOlderThanTheEventClosesNothing()
        {
            var queue = new NotificationQueue();
            queue.OnSnapshot(Snapshot());
            // Detected after the snapshot was taken: the snapshot's view of shuttle-03 is out of date
            queue.OnEvent(Detected(9, reach: new[] { ("shuttle-03", 120.0), ("shuttle-04", 40.0) }));
            queue.OnSnapshot(Snapshot());
            CollectionAssert.AreEquivalent(new[] { "shuttle-03", "shuttle-04" }, Open(queue));

            queue.OnSnapshot(Snapshot(9));
            CollectionAssert.AreEqual(new[] { "shuttle-04" }, Open(queue), "a snapshot as new as the event shows shuttle-03 decided for");
        }

        [Test]
        public void AShuttleThatNoLongerReachesItIsNoLongerNotified()
        {
            var queue = new NotificationQueue();
            var s = Snapshot();
            Expose(s, "shuttle-05", 30);
            queue.OnSnapshot(s);
            queue.OnEvent(Detected(reach: ("shuttle-05", 30.0)));
            Assert.AreEqual(1, queue.Items.Count);
            queue.OnSnapshot(Snapshot(8));
            Assert.IsEmpty(queue.Items, "shuttle-05 neither reaches it nor has options for it");
        }

        [Test]
        public void ADetectionNamingNoShuttleWaitsForTheSnapshot()
        {
            var queue = new NotificationQueue();
            // A closure placed on a shuttle's route, detected before any snapshot showed it
            Assert.IsTrue(queue.OnEvent(E("INCIDENT_DETECTED", new JObject { ["alert"] = true }, 5, Closure)));
            Assert.IsEmpty(queue.Items);
            queue.OnSnapshot(Snapshot());
            var n = queue.Items.Single();
            Assert.AreEqual((Closure, "shuttle-01"), (n.IncidentId, n.VehicleId));
            Assert.AreEqual("Road closure · Lorong 6 Toa Payoh", n.Title);
            Assert.AreEqual("Shuttle 01 reaches it in about 8 s.", n.Body);
        }

        [Test]
        public void ClosingANotificationLeavesTheShuttleOnTheStrip()
        {
            var queue = new NotificationQueue();
            var s = Snapshot();
            queue.OnSnapshot(s);
            queue.OnEvent(Detected(reach: ("shuttle-04", 40.0)));
            queue.Dismiss(Collision, "shuttle-04");
            Assert.IsEmpty(queue.Items);
            queue.OnEvent(Ready(true, "shuttle-04"));
            queue.OnSnapshot(Snapshot(8));
            Assert.IsEmpty(queue.Items, "the operator closed it; it does not come back for the same incident");
            CollectionAssert.Contains(DecisionRules.PendingVehicles(s), "shuttle-04", "the Needs a decision strip comes from the snapshot");
        }

        [Test]
        public void AnotherSessionStartsAfresh()
        {
            var queue = new NotificationQueue();
            queue.OnSnapshot(Snapshot());
            queue.OnEvent(Detected(reach: ("shuttle-04", 40.0)));
            var other = Snapshot();
            other.SessionId = "session-002";
            queue.OnSnapshot(other);
            Assert.IsEmpty(queue.Items);

            queue.OnEvent(Detected(reach: ("shuttle-04", 40.0)));
            Assert.AreEqual(1, queue.Items.Count);
            queue.Clear();
            Assert.IsEmpty(queue.Items, "leaving the session");
        }

        [Test]
        public void NotificationsPassTheAlertGateEvenMuted()
        {
            var queue = new NotificationQueue();
            queue.OnSnapshot(Snapshot());
            var played = new List<string>();
            var alerts = new TrainingAlerts(played.Add) { Muted = true };
            alerts.Fresh = e => queue.OnEvent(e);

            var history = Detected(reach: ("shuttle-04", 40.0));
            alerts.OnEvents(new[] { history }, false);
            Assert.IsEmpty(queue.Items, "history never notifies");

            var detected = Detected(reach: ("shuttle-04", 40.0));
            alerts.OnEvents(new[] { detected }, true);
            Assert.AreEqual(1, queue.Items.Count, "muted alerts still notify; only the sound is silenced");
            Assert.IsEmpty(played);

            // A reconnect replays the event: the gate has seen it, so it reaches no queue again
            var replayed = new NotificationQueue();
            replayed.OnSnapshot(Snapshot());
            alerts.Fresh = e => replayed.OnEvent(e);
            alerts.OnEvents(new[] { detected }, true);
            Assert.IsEmpty(replayed.Items, "a replayed event is not news");
        }
    }
}
