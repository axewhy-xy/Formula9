/*
 * Unit tests for BatteryWatch.cs.
 *
 * Author: Choo Jian Wei
 * Date Created: 03/11/2026
 * Last Modified: 24/11/2026
 *
 * Description:
 *     Checks when the battery critical sound plays: once when a shuttle's gauge turns red,
 *     again only after it charges back, never for what was already low on joining, and never while
 *     Mute alerts is on. Also checks the clip it plays is real.
 */

using System.Collections.Generic;
using System.Linq;
using Formula9.Audio;
using Formula9.Contracts;
using NUnit.Framework;
using UnityEngine;

namespace Formula9.Tests
{
    // -----------------------------------------------------------------------------------
    // Class: BatteryWatchTests
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class tests the battery low watch with a fake player.
    public class BatteryWatchTests
    {
        private List<string> played;
        private bool muted;
        private BatteryWatch watch;

        // -----------------------------------------------------------------------------------
        // Function: SetUp
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function gives each test a fresh list of played cues, unmuted.
        [SetUp]
        public void SetUp()
        {
            played = new List<string>();
            muted = false;
            watch = new BatteryWatch(played.Add, () => muted);
        }

        // -----------------------------------------------------------------------------------
        // Function: Snapshot
        // -----------------------------------------------------------------------------------
        // Description:
        //     This function makes a snapshot of a session whose shuttles have the given battery
        //     percentages, on 100 kWh batteries.
        //
        // Args:
        //     session (string): The session ID.
        //     percents (double[]): Each shuttle's battery, shuttle-01 first.
        //
        // Returns:
        //     FullSnapshot: The snapshot.
        private static FullSnapshot Snapshot(string session, params double[] percents) => new FullSnapshot
        {
            SessionId = session,
            Vehicles = percents.Select((p, i) => new FleetVehicle { VehicleId = $"shuttle-0{i + 1}", BatteryCapacityKwh = 100, BatteryKwh = p }).ToList(),
        };

        // -----------------------------------------------------------------------------------
        // Function: DroppingBelowTheThresholdSoundsOnce
        // -----------------------------------------------------------------------------------
        // Description:
        //     A shuttle whose gauge turns red sounds once, and staying red stays silent.
        [Test]
        public void DroppingBelowTheThresholdSoundsOnce()
        {
            watch.OnSnapshot(Snapshot("s", 25, 70));
            watch.OnSnapshot(Snapshot("s", 14, 70));
            watch.OnSnapshot(Snapshot("s", 10, 70));
            CollectionAssert.AreEqual(new[] { BatteryWatch.Cue }, played);
        }

        // -----------------------------------------------------------------------------------
        // Function: ItSoundsOnlyWhenTheGaugeTurnsRed
        // -----------------------------------------------------------------------------------
        // Description:
        //     The sound is the critical warning, so a battery at exactly RedPercent, whose gauge is
        //     not red yet, stays silent; just below it, the gauge turns red and it sounds.
        [Test]
        public void ItSoundsOnlyWhenTheGaugeTurnsRed()
        {
            watch.OnSnapshot(Snapshot("s", 21));
            watch.OnSnapshot(Snapshot("s", 20));
            Assert.IsEmpty(played, "20% is not red yet");
            watch.OnSnapshot(Snapshot("s", 19.99));
            CollectionAssert.AreEqual(new[] { BatteryWatch.Cue }, played, "19.99% is red");
        }

        // -----------------------------------------------------------------------------------
        // Function: RedMatchesTheGaugeColourAtTheBoundary
        // -----------------------------------------------------------------------------------
        // Description:
        //     Red starts just below 20%. The sound and the Charging page's gauge class are each
        //     checked against that fixed boundary, not against each other, so a change to either
        //     one fails here. Both pages draw BatteryWatch.Percent, which the sound reads too.
        [Test]
        public void RedMatchesTheGaugeColourAtTheBoundary()
        {
            var expected = new Dictionary<double, bool> { [0] = true, [19.5] = true, [19.9999] = true, [20] = false, [20.0001] = false, [21] = false };
            foreach (var entry in expected)
            {
                var vehicle = Snapshot("s", entry.Key).Vehicles[0];
                Assert.AreEqual(entry.Value, BatteryWatch.IsRed(vehicle), $"{entry.Key}% sounds");
                Assert.AreEqual(entry.Key, BatteryWatch.Percent(vehicle), 1e-9, $"{entry.Key}% gauge percent");
                Assert.AreEqual(entry.Value, Formula9.UI.ChargingPresenter.BatteryClass(entry.Key) == "charge-battery-low", $"{entry.Key}% Charging page gauge");
            }
        }

        // -----------------------------------------------------------------------------------
        // Function: AShuttleWithNoCapacityIsRedLikeItsGauge
        // -----------------------------------------------------------------------------------
        // Description:
        //     A shuttle with no battery capacity shows a red 0% bar, so it counts as red and
        //     sounds like any other shuttle whose gauge turns red.
        [Test]
        public void AShuttleWithNoCapacityIsRedLikeItsGauge()
        {
            var empty = new FleetVehicle { BatteryCapacityKwh = 0, BatteryKwh = 0 };
            Assert.AreEqual(0, BatteryWatch.Percent(empty));
            Assert.IsTrue(BatteryWatch.IsRed(empty));
            Assert.AreEqual("charge-battery-low", Formula9.UI.ChargingPresenter.BatteryClass(BatteryWatch.Percent(empty)));
        }

        // -----------------------------------------------------------------------------------
        // Function: AfterAReconnectADropWaitsOneSnapshot
        // -----------------------------------------------------------------------------------
        // Description:
        //     A drop in the snapshot that ends an outage sounds one snapshot later instead of on
        //     top of the session recovered alert; a hold with no drop changes nothing after it.
        [Test]
        public void AfterAReconnectADropWaitsOneSnapshot()
        {
            watch.OnSnapshot(Snapshot("s", 30, 30));
            watch.HoldNextCue();
            watch.OnSnapshot(Snapshot("s", 10, 30));
            Assert.IsEmpty(played, "held on the reconnect snapshot");
            watch.OnSnapshot(Snapshot("s", 10, 30));
            CollectionAssert.AreEqual(new[] { BatteryWatch.Cue }, played, "sounds on the next one");

            watch.HoldNextCue();
            watch.OnSnapshot(Snapshot("s", 10, 30));
            watch.OnSnapshot(Snapshot("s", 10, 10));
            Assert.AreEqual(2, played.Count, "a hold with no drop does not delay a later drop");
        }

        // -----------------------------------------------------------------------------------
        // Function: ChargingBackUpRearmsIt
        // -----------------------------------------------------------------------------------
        // Description:
        //     After a shuttle charges back above the threshold, running low again sounds again.
        [Test]
        public void ChargingBackUpRearmsIt()
        {
            watch.OnSnapshot(Snapshot("s", 25));
            watch.OnSnapshot(Snapshot("s", 12));
            watch.OnSnapshot(Snapshot("s", 90));
            watch.OnSnapshot(Snapshot("s", 12));
            Assert.AreEqual(2, played.Count);
        }

        // -----------------------------------------------------------------------------------
        // Function: ShuttlesAlreadyLowOnJoiningStaySilent
        // -----------------------------------------------------------------------------------
        // Description:
        //     The first snapshot of a session, including a newly joined one, only records what is
        //     already low.
        [Test]
        public void ShuttlesAlreadyLowOnJoiningStaySilent()
        {
            watch.OnSnapshot(Snapshot("a", 12));
            watch.OnSnapshot(Snapshot("b", 70, 10));
            Assert.IsEmpty(played);
            watch.OnSnapshot(Snapshot("b", 14, 10));
            CollectionAssert.AreEqual(new[] { BatteryWatch.Cue }, played, "only shuttle-01's drop sounds");
        }

        // -----------------------------------------------------------------------------------
        // Function: SeveralDropsInOneSnapshotSoundOnce
        // -----------------------------------------------------------------------------------
        // Description:
        //     Two shuttles dropping in the same snapshot play one sound, not two on top of each
        //     other.
        [Test]
        public void SeveralDropsInOneSnapshotSoundOnce()
        {
            watch.OnSnapshot(Snapshot("s", 30, 30));
            watch.OnSnapshot(Snapshot("s", 10, 10));
            Assert.AreEqual(1, played.Count);
        }

        // -----------------------------------------------------------------------------------
        // Function: MuteAlertsSilencesIt
        // -----------------------------------------------------------------------------------
        // Description:
        //     A drop while Mute alerts is on is silent and does not sound later when unmuted.
        [Test]
        public void MuteAlertsSilencesIt()
        {
            watch.OnSnapshot(Snapshot("s", 30));
            muted = true;
            watch.OnSnapshot(Snapshot("s", 10));
            muted = false;
            watch.OnSnapshot(Snapshot("s", 9));
            Assert.IsEmpty(played);
        }

        // -----------------------------------------------------------------------------------
        // Function: AnEmptyOrMissingSnapshotIsIgnored
        // -----------------------------------------------------------------------------------
        // Description:
        //     No snapshot, or one without shuttles or battery capacity, plays nothing.
        [Test]
        public void AnEmptyOrMissingSnapshotIsIgnored()
        {
            watch.OnSnapshot(null);
            watch.OnSnapshot(new FullSnapshot { SessionId = "s" });
            Assert.IsEmpty(played);
        }

        // -----------------------------------------------------------------------------------
        // Function: EveryClientCueHasARealClip
        // -----------------------------------------------------------------------------------
        // Description:
        //     Battery, disconnect and reconnect each map to their own clip that loads.
        [Test]
        public void EveryClientCueHasARealClip()
        {
            CollectionAssert.AreEquivalent(new[] { BatteryWatch.Cue, AlertPlayer.DisconnectCue, AlertPlayer.ReconnectCue }, AlertPlayer.ClientSoundFiles.Keys);
            Assert.AreEqual(AlertPlayer.ClientSoundFiles.Count, AlertPlayer.ClientSoundFiles.Values.Distinct().Count(), "one distinct sound per cue");
            foreach (var file in AlertPlayer.ClientSoundFiles.Values)
            {
                var clip = Resources.Load<AudioClip>($"Formula9/Audio/{file}");
                Assert.IsNotNull(clip, file);
                Assert.Greater(clip.length, 0.02f, file);
            }
        }
    }
}
