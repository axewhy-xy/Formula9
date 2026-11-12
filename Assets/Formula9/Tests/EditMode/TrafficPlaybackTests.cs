/*
 * Unit tests for the clock the 3D city draws the backend's traffic on (TrafficPlaybackClock.cs).
 *
 * Author: Lau Xiang Yang
 * Date Created: 11/11/2026
 * Last Modified: 11/11/2026
 *
 * Description:
 *     Feeds the clock frames ten a session second as a backend would, arriving late or early by a
 *     made-up but repeatable jitter, and moves it on every frame drawn. Covers keeping an even speed
 *     through jittery arrivals without ever standing still, following a backend that runs slower
 *     than the wall clock, waiting for a lead when there are no earlier frames, slowing to a stop at
 *     the newest frame when frames stop, widening its margin after a late frame, and starting over
 *     when far behind.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Formula9.World;
using NUnit.Framework;

namespace Formula9.Tests
{
    public class TrafficPlaybackTests
    {
        private const double Frame = 1 / 60.0;

        // Plays the clock for a while: a frame each interval of session time, made every interval / pace
        // real seconds and arriving up to jitterS late, drawn every Frame. Returns the clock's speed at
        // each frame drawn after the first warmupS seconds.
        private static List<double> Play(TrafficPlaybackClock clock, double seconds, double pace = 1, double jitterS = 0,
            double warmupS = 3, int seed = 7, double startS = 0, double startTimeS = 0)
        {
            var random = new Random(seed);
            var speeds = new List<double>();
            var made = 0;
            var pending = new List<(double At, double TimeS)>();
            for (var now = startS; now < startS + seconds; now += Frame)
            {
                while ((made + 1) * 0.1 / pace <= now - startS)
                {
                    made++;
                    pending.Add((startS + made * 0.1 / pace + random.NextDouble() * jitterS, startTimeS + made * 0.1));
                }
                // Replies come in order: a frame never overtakes the one before it
                while (pending.Count > 0 && pending[0].At <= now)
                {
                    clock.Add(pending[0].TimeS, now);
                    pending.RemoveAt(0);
                }
                var before = clock.TimeS;
                clock.Advance(Frame);
                Assert.LessOrEqual(clock.TimeS, clock.NewestS, "never past the newest frame");
                if (!double.IsNaN(before)) Assert.GreaterOrEqual(clock.TimeS, before, "never back");
                if (now - startS >= warmupS) speeds.Add(clock.Speed);
            }
            return speeds;
        }

        [Test]
        public void FramesThatComeOnTimeAreDrawnAtTheWallClocksPace()
        {
            var clock = new TrafficPlaybackClock();
            var speeds = Play(clock, 20);
            Assert.AreEqual(1, clock.Pace, 0.02);
            Assert.That(speeds, Has.All.InRange(0.97, 1.03));
            Assert.AreEqual(0, clock.NearStalls);
            Assert.Less(clock.LeadS, 0.1 + TrafficPlaybackClock.StartMarginS + 0.03, "close behind the newest frame");
        }

        [Test]
        public void JitteryFramesAreDrawnAtAnEvenSpeedThatNeverStops()
        {
            var clock = new TrafficPlaybackClock();
            // Each frame up to 80 ms late, at random
            var speeds = Play(clock, 60, jitterS: 0.08, warmupS: 10);
            Assert.That(speeds, Has.All.InRange(1 - TrafficPlaybackClock.MaxCorrection - 0.01, 1 + TrafficPlaybackClock.MaxCorrection + 0.01),
                "never stands still, nor rushes");
            Assert.AreEqual(1, speeds.Average(), 0.02);
            Assert.LessOrEqual(clock.NearStalls, 2, "the lowest lead allows for the late frames");
        }

        [Test]
        public void ABackendRunningBehindTheWallClockIsFollowedAtItsOwnPace()
        {
            var clock = new TrafficPlaybackClock();
            // Ten frames a session second, but only 7.7 a real second, with some jitter
            var speeds = Play(clock, 30, pace: 0.77, jitterS: 0.03, warmupS: 8);
            Assert.AreEqual(0.77, clock.Pace, 0.03);
            Assert.AreEqual(0.77, speeds.Average(), 0.03);
            Assert.That(speeds, Has.All.GreaterThan(0.6), "it slows to the backend's pace, not to a stop");
            var stalls = clock.NearStalls;
            Play(clock, 20, pace: 0.77, jitterS: 0.03, warmupS: 0, startS: 30, startTimeS: clock.NewestS);
            Assert.AreEqual(stalls, clock.NearStalls, "and then keeps up without catching the newest frame");
        }

        [Test]
        public void WithNoEarlierFramesTheClockWaitsForItsLead()
        {
            var clock = new TrafficPlaybackClock();
            clock.Add(10, 0);
            clock.Advance(Frame);
            clock.HoldAtLeast(10);
            Assert.AreEqual(10, clock.TimeS, 1e-9, "at the only frame there is");
            clock.Add(10.1, 0.1);
            clock.Advance(Frame);
            clock.HoldAtLeast(10);
            Assert.AreEqual(10, clock.TimeS, 1e-9, "still waiting");
            clock.Add(10.2, 0.2);
            clock.Advance(Frame);
            Assert.Greater(clock.TimeS, 10, "a frame and the margin ahead: off it goes");
        }

        [Test]
        public void WhenFramesStopTheClockSlowsToAStopAtTheNewest()
        {
            var clock = new TrafficPlaybackClock();
            Play(clock, 5);
            var newest = clock.NewestS;
            var last = clock.Speed;
            for (var i = 0; i < 60; i++)
            {
                clock.Advance(Frame);
                Assert.LessOrEqual(clock.Speed, last + 0.005, "only ever slower");
                last = clock.Speed;
            }
            Assert.AreEqual(newest, clock.TimeS, 1e-3);
            Assert.Less(clock.Speed, 0.01);
        }

        [Test]
        public void AFrameSoLateTheClockNearlyCaughtTheNewestWidensTheMargin()
        {
            var clock = new TrafficPlaybackClock();
            Play(clock, 10);
            var margin = clock.MarginS;
            // Nothing for half a second
            for (var i = 0; i < 30; i++) clock.Advance(Frame);
            Assert.AreEqual(1, clock.NearStalls);
            Assert.AreEqual(margin + TrafficPlaybackClock.MarginGrowthS, clock.MarginS, 1e-3);
            // The late frame comes, and frames carry on: the wider margin stays
            clock.Add(clock.NewestS + 0.1, 10.5);
            Assert.AreEqual(margin + TrafficPlaybackClock.MarginGrowthS, clock.MarginS, 1e-3);
        }

        [Test]
        public void APauseIsNotTakenForALateFrame()
        {
            var clock = new TrafficPlaybackClock();
            Play(clock, 10);
            var margin = clock.MarginS;
            // The session pauses for two seconds: the clock stops at the newest frame, then frames come again
            for (var i = 0; i < 120; i++) clock.Advance(Frame);
            clock.Add(clock.NewestS + 0.1, 12);
            Assert.AreEqual(0, clock.NearStalls);
            Assert.AreEqual(margin, clock.MarginS, 1e-3);
        }

        [Test]
        public void AClockFarBehindStartsOverJustBehindTheNewest()
        {
            var clock = new TrafficPlaybackClock();
            Play(clock, 3);
            // Three seconds of frames at once, as after the view was hidden
            var newest = clock.NewestS;
            for (var k = 1; k <= 30; k++) clock.Add(newest + k * 0.1, 3.0);
            clock.Advance(Frame);
            Assert.AreEqual(clock.NewestS - 0.1 - clock.MarginS, clock.TimeS, 0.01);
        }

        [Test]
        public void FramesNoNewerThanTheNewestAreLeftOut()
        {
            var clock = new TrafficPlaybackClock();
            clock.Add(10, 0);
            clock.Add(9.9, 0.1);
            clock.Add(10, 0.1);
            Assert.AreEqual(10, clock.NewestS);
            clock.Reset();
            Assert.IsFalse(clock.Started);
            Assert.IsNaN(clock.NewestS);
        }
    }
}
