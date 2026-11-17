/*
 * Unit tests for the training alerts (TrainingAlerts.cs, AlertPlayer.cs, Preferences.cs).
 *
 * Author: Choo Jian Wei
 * Date Created: 30/10/2026
 * Last Modified: 30/10/2026
 *
 * Description:
 *     Mirrors the Qt client's AlertGateTest, TrainingAlertsTest and TrainingAlertSoundsTest suites:
 *     which events alert, each event ID at most once, silent history, muting without a burst on
 *     unmute, bounded memory, the shared cue table, the saved mute setting, and a real sound file
 *     for every cue.
 */

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
    // -----------------------------------------------------------------------------------
    // Class: AlertTests
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class tests the alert rules and sounds.
    public class AlertTests
    {
        // -----------------------------------------------------------------------------------
        // Function: E
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function makes a training event.
        //
        // Args:
        //     id (string): The event ID.
        //     entity (string): The entity type.
        //     type (string): The event type.
        //
        // Returns:
        //     Event: The event.
        private static Contracts.Event E(string id, string entity, string type) =>
            new Contracts.Event { EventId = id, EntityType = entity, EventType = type };

        // -----------------------------------------------------------------------------------
        // Function: Incident
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function makes an incident OPTIONS_READY event, which alerts.
        //
        // Args:
        //     id (string): The event ID.
        //
        // Returns:
        //     Event: The event.
        private static Contracts.Event Incident(string id = "event-1") => E(id, "INCIDENT", "OPTIONS_READY");

        // -----------------------------------------------------------------------------------
        // Function: Fixture
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function reads a shared contract fixture.
        //
        // Args:
        //     name (string): The fixture name, without .json.
        //
        // Returns:
        //     string: The fixture's JSON.
        private static string Fixture(string name) =>
            File.ReadAllText(Path.Combine(Application.dataPath, "Formula9/Tests/EditMode/Fixtures", name + ".json"));

        // -----------------------------------------------------------------------------------
        // Function: MeaningfulLiveEventPlaysItsSound
        // -----------------------------------------------------------------------------------
        // Description:
        //     A live incident event plays the incident cue.
        [Test]
        public void MeaningfulLiveEventPlaysItsSound() =>
            Assert.AreEqual("incident_alert", new AlertGate().OnEvent(Incident(), true));

        // -----------------------------------------------------------------------------------
        // Function: RoutineEventsAreSilent
        // -----------------------------------------------------------------------------------
        // Description:
        //     Routine events such as COMMAND_APPLIED, ROUTE_APPROVED and SESSION_READY play
        //     nothing.
        [Test]
        public void RoutineEventsAreSilent()
        {
            var gate = new AlertGate();
            Assert.IsNull(gate.OnEvent(E("a", "COMMAND", "COMMAND_APPLIED"), true));
            Assert.IsNull(gate.OnEvent(E("b", "ROUTE", "ROUTE_APPROVED"), true));
            Assert.IsNull(gate.OnEvent(E("c", "SESSION", "SESSION_READY"), true));
        }

        // -----------------------------------------------------------------------------------
        // Function: CueKeyNeedsEntityAndEventType
        // -----------------------------------------------------------------------------------
        // Description:
        //     A cue matches on entity type and event type together, not event type alone.
        [Test]
        public void CueKeyNeedsEntityAndEventType() =>
            Assert.IsNull(new AlertGate().OnEvent(E("a", "VEHICLE", "OPTIONS_READY"), true));

        // -----------------------------------------------------------------------------------
        // Function: EveryDefaultCueIsReachableAndCoversM2AttentionEvents
        // -----------------------------------------------------------------------------------
        // Description:
        //     The default cue table is exactly the M2 attention events, and each one plays its cue.
        [Test]
        public void EveryDefaultCueIsReachableAndCoversM2AttentionEvents()
        {
            var expected = new Dictionary<string, string>
            {
                ["INCIDENT/OPTIONS_READY"] = "incident_alert",
                ["INCIDENT/INCIDENT_DETECTED"] = "incident_alert",
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

        // -----------------------------------------------------------------------------------
        // Function: SharedCueContractIsEmbedded
        // -----------------------------------------------------------------------------------
        // Description:
        //     The embedded cue table matches the shared alert_cues.json fixture.
        [Test]
        public void SharedCueContractIsEmbedded()
        {
            var shared = JObject.Parse(Fixture("alert_cues"));
            CollectionAssert.AreEquivalent(shared.Properties().ToDictionary(p => p.Name, p => (string)p.Value), AlertCues.Default);
        }

        // -----------------------------------------------------------------------------------
        // Function: SameEventIdAlertsOnlyOnce
        // -----------------------------------------------------------------------------------
        // Description:
        //     An event replayed after a reconnect does not alert again.
        [Test]
        public void SameEventIdAlertsOnlyOnce()
        {
            var gate = new AlertGate();
            Assert.IsNotNull(gate.OnEvent(Incident(), true));
            Assert.IsNull(gate.OnEvent(Incident(), true), "a reconnect replays the same event");
        }

        // -----------------------------------------------------------------------------------
        // Function: HistoryPlaybackIsSilentAndNotRemembered
        // -----------------------------------------------------------------------------------
        // Description:
        //     History playback is silent and does not stop the same event alerting when live.
        [Test]
        public void HistoryPlaybackIsSilentAndNotRemembered()
        {
            var gate = new AlertGate();
            Assert.IsNull(gate.OnEvent(Incident(), false));
            Assert.AreEqual("incident_alert", gate.OnEvent(Incident(), true));
        }

        // -----------------------------------------------------------------------------------
        // Function: MuteSilencesWithoutBurstOnUnmute
        // -----------------------------------------------------------------------------------
        // Description:
        //     Events seen while muted stay silent after unmuting; new events alert.
        [Test]
        public void MuteSilencesWithoutBurstOnUnmute()
        {
            var gate = new AlertGate { Muted = true };
            Assert.IsNull(gate.OnEvent(Incident("a"), true));
            gate.Muted = false;
            Assert.IsNull(gate.OnEvent(Incident("a"), true), "a muted event is already seen");
            Assert.AreEqual("incident_alert", gate.OnEvent(Incident("b"), true));
        }

        // -----------------------------------------------------------------------------------
        // Function: MemoryLimitForgetsOldestFirst
        // -----------------------------------------------------------------------------------
        // Description:
        //     Past the memory limit the oldest event ID is forgotten first.
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

        // -----------------------------------------------------------------------------------
        // Function: RoutineEventsAreRememberedToo
        // -----------------------------------------------------------------------------------
        // Description:
        //     Routine events count towards the memory limit like any other.
        [Test]
        public void RoutineEventsAreRememberedToo()
        {
            var gate = new AlertGate(null, 1);
            gate.OnEvent(Incident("a"), true);
            gate.OnEvent(E("routine", "COMMAND", "COMMAND_APPLIED"), true);
            Assert.AreEqual("incident_alert", gate.OnEvent(Incident("a"), true), "the routine event pushed it out");
        }

        // -----------------------------------------------------------------------------------
        // Function: CustomCueTable
        // -----------------------------------------------------------------------------------
        // Description:
        //     A gate can be given its own cue table.
        [Test]
        public void CustomCueTable() =>
            Assert.AreEqual("custom", new AlertGate(new Dictionary<string, string> { ["VEHICLE/BLOCKED"] = "custom" })
                .OnEvent(E("a", "VEHICLE", "BLOCKED"), true));

        // -----------------------------------------------------------------------------------
        // Function: SharedFixtureReplayEventAlertsOnce
        // -----------------------------------------------------------------------------------
        // Description:
        //     The shared replay fixture's incident alerts once even when the batch arrives twice.
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

        // -----------------------------------------------------------------------------------
        // Function: AnIncidentNoShuttleReachesStaysSilent
        // -----------------------------------------------------------------------------------
        // Description:
        //     A detected incident alerts only when the backend says a shuttle will reach it.
        [Test]
        public void AnIncidentNoShuttleReachesStaysSilent()
        {
            var gate = new AlertGate();
            var quiet = E("q", "INCIDENT", "INCIDENT_DETECTED"); quiet.Data = new JObject { ["alert"] = false };
            var loud = E("l", "INCIDENT", "INCIDENT_DETECTED"); loud.Data = new JObject { ["alert"] = true };
            Assert.IsNull(gate.OnEvent(quiet, true));
            Assert.AreEqual("incident_alert", gate.OnEvent(loud, true));
        }

        // -----------------------------------------------------------------------------------
        // Function: EachLiveEventIsReportedOnceEvenWhenMuted
        // -----------------------------------------------------------------------------------
        // Description:
        //     The screen hears of each live event once, muted or not, and never of history.
        [Test]
        public void EachLiveEventIsReportedOnceEvenWhenMuted()
        {
            var played = new List<string>();
            var fresh = new List<string>();
            var alerts = new TrainingAlerts(played.Add) { Muted = true, Fresh = e => fresh.Add(e.EventId) };
            alerts.OnEvents(new[] { Incident("1") }, false);
            alerts.OnEvents(new[] { Incident("1"), Incident("2") }, true);
            alerts.OnEvents(new[] { Incident("2") }, true);
            CollectionAssert.AreEqual(new[] { "1", "2" }, fresh);
            Assert.IsEmpty(played);
        }

        // -----------------------------------------------------------------------------------
        // Function: LiveBatchPlaysMeaningfulCuesInOrder
        // -----------------------------------------------------------------------------------
        // Description:
        //     A live batch plays its meaningful cues in event order and skips routine ones.
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

        // -----------------------------------------------------------------------------------
        // Function: HistoryIsSilentThenLiveAlerts
        // -----------------------------------------------------------------------------------
        // Description:
        //     TrainingAlerts keeps history silent, then alerts for the same event live.
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

        // -----------------------------------------------------------------------------------
        // Function: MissingPlayerDoesNotThrow
        // -----------------------------------------------------------------------------------
        // Description:
        //     Alerts without a player do nothing instead of throwing.
        [Test]
        public void MissingPlayerDoesNotThrow() =>
            Assert.DoesNotThrow(() => new TrainingAlerts(null).OnEvents(new[] { Incident() }, true));

        // -----------------------------------------------------------------------------------
        // Function: MuteIsRestoredAndSavedWithoutTouchingRealSettings
        // -----------------------------------------------------------------------------------
        // Description:
        //     Mute alerts is saved on change and restored on the next start, using in-memory
        //     preferences so the real settings are untouched.
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

        // -----------------------------------------------------------------------------------
        // Function: EveryCueHasItsOwnExistingSoundFile
        // -----------------------------------------------------------------------------------
        // Description:
        //     Every cue has its own distinct sound file, and each loads as a real clip.
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
