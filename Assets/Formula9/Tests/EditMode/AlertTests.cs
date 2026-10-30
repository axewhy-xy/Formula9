using System.Collections.Generic;
using System.IO;
using System.Linq;
using Formula9.Audio;
using Formula9.Contracts;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Formula9.Tests
{
    // Mirrors the Qt AlertGateTest, TrainingAlertsTest and TrainingAlertSoundsTest suites.
    public class AlertTests
    {
        private static Contracts.Event E(string id, string entity, string type) =>
            new Contracts.Event { EventId = id, EntityType = entity, EventType = type };

        private static Contracts.Event Incident(string id = "event-1") => E(id, "INCIDENT", "OPTIONS_READY");

        private static string Fixture(string name) =>
            File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures", name + ".json"));

        [Test]
        public void MeaningfulLiveEventPlaysItsSound() =>
            Assert.AreEqual("incident_alert", new AlertGate().OnEvent(Incident(), true));

        [Test]
        public void RoutineEventsAreSilent()
        {
            var gate = new AlertGate();
            Assert.IsNull(gate.OnEvent(E("a", "COMMAND", "COMMAND_APPLIED"), true));
            Assert.IsNull(gate.OnEvent(E("b", "ROUTE", "ROUTE_APPROVED"), true));
            Assert.IsNull(gate.OnEvent(E("c", "SESSION", "SESSION_READY"), true));
        }

        [Test]
        public void CueKeyNeedsEntityAndEventType() =>
            Assert.IsNull(new AlertGate().OnEvent(E("a", "VEHICLE", "OPTIONS_READY"), true));

        [Test]
        public void EveryDefaultCueIsReachableAndCoversM2AttentionEvents()
        {
            var expected = new Dictionary<string, string>
            {
                ["INCIDENT/OPTIONS_READY"] = "incident_alert",
                ["COMMAND/COMMAND_REJECTED"] = "command_rejected",
                ["COMMAND/COMMAND_NEEDS_RECONFIRMATION"] = "needs_reconfirmation",
                ["SESSION/SESSION_RECOVERED"] = "session_recovered",
            };
            CollectionAssert.AreEquivalent(expected, AlertCues.Default);
            var gate = new AlertGate();
            var i = 0;
            foreach (var entry in AlertCues.Default)
            {
                var parts = entry.Key.Split('/');
                Assert.AreEqual(entry.Value, gate.OnEvent(E($"event-{i++}", parts[0], parts[1]), true));
            }
        }

        [Test]
        public void SharedCueContractIsEmbedded()
        {
            var shared = JObject.Parse(Fixture("alert_cues"));
            CollectionAssert.AreEquivalent(shared.Properties().ToDictionary(p => p.Name, p => (string)p.Value), AlertCues.Default);
        }

        [Test]
        public void SameEventIdAlertsOnlyOnce()
        {
            var gate = new AlertGate();
            Assert.IsNotNull(gate.OnEvent(Incident(), true));
            Assert.IsNull(gate.OnEvent(Incident(), true), "a reconnect replays the same event");
        }

        [Test]
        public void HistoryPlaybackIsSilentAndNotRemembered()
        {
            var gate = new AlertGate();
            Assert.IsNull(gate.OnEvent(Incident(), false));
            Assert.AreEqual("incident_alert", gate.OnEvent(Incident(), true));
        }

        [Test]
        public void MuteSilencesWithoutBurstOnUnmute()
        {
            var gate = new AlertGate { Muted = true };
            Assert.IsNull(gate.OnEvent(Incident("a"), true));
            gate.Muted = false;
            Assert.IsNull(gate.OnEvent(Incident("a"), true), "a muted event is already seen");
            Assert.AreEqual("incident_alert", gate.OnEvent(Incident("b"), true));
        }

        [Test]
        public void MemoryLimitForgetsOldestFirst()
        {
            var gate = new AlertGate(null, 2);
            gate.OnEvent(Incident("a"), true);
            gate.OnEvent(Incident("b"), true);
            gate.OnEvent(Incident("c"), true);
            Assert.IsNull(gate.OnEvent(Incident("c"), true));
            Assert.AreEqual("incident_alert", gate.OnEvent(Incident("a"), true), "the oldest ID was forgotten");
        }

        [Test]
        public void RoutineEventsAreRememberedToo()
        {
            var gate = new AlertGate(null, 1);
            gate.OnEvent(Incident("a"), true);
            gate.OnEvent(E("routine", "COMMAND", "COMMAND_APPLIED"), true);
            Assert.AreEqual("incident_alert", gate.OnEvent(Incident("a"), true), "the routine event pushed it out");
        }

        [Test]
        public void CustomCueTable() =>
            Assert.AreEqual("custom", new AlertGate(new Dictionary<string, string> { ["VEHICLE/BLOCKED"] = "custom" })
                .OnEvent(E("a", "VEHICLE", "BLOCKED"), true));

        [Test]
        public void SharedFixtureReplayEventAlertsOnce()
        {
            var replay = (EventReplay)ContractParser.ParseMessage(Fixture("event_replay"));
            var played = new List<string>();
            var alerts = new TrainingAlerts(played.Add);
            alerts.OnEvents(replay.Events, true);
            alerts.OnEvents(replay.Events, true);
            CollectionAssert.AreEqual(new[] { "incident_alert" }, played);
        }

        [Test]
        public void LiveBatchPlaysMeaningfulCuesInOrder()
        {
            var played = new List<string>();
            new TrainingAlerts(played.Add).OnEvents(new[]
            {
                E("1", "COMMAND", "COMMAND_REJECTED"), E("2", "COMMAND", "COMMAND_APPLIED"),
                Incident("3"), E("4", "SESSION", "SESSION_RECOVERED"),
            }, true);
            CollectionAssert.AreEqual(new[] { "command_rejected", "incident_alert", "session_recovered" }, played);
        }

        [Test]
        public void HistoryIsSilentThenLiveAlerts()
        {
            var played = new List<string>();
            var alerts = new TrainingAlerts(played.Add);
            alerts.OnEvents(new[] { Incident() }, false);
            Assert.IsEmpty(played);
            alerts.OnEvents(new[] { Incident() }, true);
            CollectionAssert.AreEqual(new[] { "incident_alert" }, played);
        }

        [Test]
        public void MissingPlayerDoesNotThrow() =>
            Assert.DoesNotThrow(() => new TrainingAlerts(null).OnEvents(new[] { Incident() }, true));

        [Test]
        public void MuteIsRestoredAndSavedWithoutTouchingRealSettings()
        {
            var preferences = new MemoryPreferences();
            var first = new MuteSetting(preferences, new TrainingAlerts(null));
            Assert.IsFalse(first.Muted);
            first.Muted = true;
            Assert.IsTrue(preferences.Values[MuteSetting.Key]);

            var played = new List<string>();
            var restarted = new MuteSetting(preferences, new TrainingAlerts(played.Add));
            Assert.IsTrue(restarted.Muted, "the choice survives a restart");
            restarted.Muted = false;
            Assert.IsFalse(preferences.Values[MuteSetting.Key]);
        }

        [Test]
        public void EveryCueHasItsOwnExistingSoundFile()
        {
            CollectionAssert.AreEquivalent(AlertCues.Default.Values.Distinct(), AlertPlayer.SoundFiles.Keys);
            Assert.AreEqual(AlertPlayer.SoundFiles.Count, AlertPlayer.SoundFiles.Values.Distinct().Count(), "one distinct sound per cue");
            foreach (var file in AlertPlayer.SoundFiles.Values)
            {
                var clip = Resources.Load<AudioClip>($"Formula9/Audio/{file}");
                Assert.IsNotNull(clip, file);
                Assert.Greater(clip.length, 0.05f, file);
            }
        }
    }
}
