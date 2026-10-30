/*
 * Unit tests for the 3D view's shuttle glides (ShuttleMotion.cs).
 *
 * Author: Lau Xiang Yang
 * Date Created: 31/10/2026
 * Last Modified: 31/10/2026
 *
 * Description:
 *     Checks that shuttles move steadily between snapshots at any snapshot rate, never go past the
 *     backend's last pose, follow the route around a bend, and jump across a discontinuity.
 */

using System.Collections.Generic;
using Formula9.World;
using NUnit.Framework;
using UnityEngine;

namespace Formula9.Tests
{
    // -----------------------------------------------------------------------------------
    // Class: ShuttleMotionTests
    // -----------------------------------------------------------------------------------
    // Description:
    //     This class tests ShuttleMotion with an explicit clock.
    public class ShuttleMotionTests
    {
        private const string Id = "shuttle-01";

        private static Vector3 Shown(ShuttleMotion motion, float now)
        {
            Assert.IsTrue(motion.Sample(Id, now, out var position, out _));
            return position;
        }

        private static void Near(Vector3 expected, Vector3 actual, float tolerance = 0.001f) =>
            Assert.Less(Vector3.Distance(expected, actual), tolerance, $"expected {expected}, was {actual}");

        private static Vector3 East(float metres) => new Vector3(metres, 0, 0);

        [Test]
        public void AShuttleWithNoPoseHasNothingToDraw() =>
            Assert.IsFalse(new ShuttleMotion().Sample(Id, 0, out _, out _));

        [Test]
        public void TheFirstPoseIsPlacedAtOnce()
        {
            var motion = new ShuttleMotion();
            motion.SetTarget(Id, East(50), Quaternion.identity, 0);
            Near(East(50), Shown(motion, 0));
        }

        [Test]
        public void AShuttleMovesSteadilyBetweenSnapshots()
        {
            var motion = new ShuttleMotion();
            motion.SetTarget(Id, East(0), Quaternion.identity, 0);
            motion.SetTarget(Id, East(11), Quaternion.identity, 1);
            // The default gap is 1 s, with a tenth of slack, so 10 m every second
            Near(East(0), Shown(motion, 1));
            Near(East(2.5f), Shown(motion, 1.25f));
            Near(East(5), Shown(motion, 1.5f));
            Near(East(7.5f), Shown(motion, 1.75f));
        }

        // A smooth view is one with no stop-and-go: the old easing covered nearly the whole distance
        // in the first half of each gap, then sat still.
        [TestCase(1f)]
        [TestCase(0.1f)]
        public void ASteadyFleetMovesAtItsTrueSpeedWithNoStall(float gap)
        {
            const float speed = 10;
            var motion = new ShuttleMotion();
            var now = 0f; var tick = 0L;
            for (var i = 0; i < 40; i++)
            {
                motion.BeginSnapshot(tick++, now);
                motion.SetTarget(Id, East(speed * now), Quaternion.identity, now);
                now += gap;
                if (i < 30) continue;
                // Once it has learned the gap, every step of the frame loop covers the same distance
                var before = Shown(motion, now - gap);
                const int frames = 6;
                for (var frame = 1; frame <= frames; frame++)
                {
                    var after = Shown(motion, now - gap + gap * frame / frames);
                    Assert.AreEqual(speed * gap / frames, after.x - before.x, speed * gap / frames * 0.05f, $"gap {gap}, frame {frame}");
                    before = after;
                }
            }
        }

        [Test]
        public void AShuttleNeverGoesPastTheLastPose()
        {
            var motion = new ShuttleMotion();
            motion.SetTarget(Id, East(0), Quaternion.identity, 0);
            motion.SetTarget(Id, East(10), Quaternion.identity, 1);
            Near(East(10), Shown(motion, 2.1f));
            Near(East(10), Shown(motion, 500));
        }

        [Test]
        public void TheSameSnapshotAgainDoesNotRestartTheGlide()
        {
            var motion = new ShuttleMotion();
            motion.SetTarget(Id, East(0), Quaternion.identity, 0);
            motion.SetTarget(Id, East(11), Quaternion.identity, 1);
            motion.BeginSnapshot(7, 1);
            motion.BeginSnapshot(7, 1.5f);
            motion.SetTarget(Id, East(11), Quaternion.identity, 1.5f);
            Assert.AreEqual(1, motion.Interval, 1e-5f, "a repeated tick teaches nothing about the gap");
            Near(East(7.5f), Shown(motion, 1.75f));
        }

        [Test]
        public void ANewPoseMidGlideContinuesFromWhereTheShuttleIsShown()
        {
            var motion = new ShuttleMotion();
            motion.SetTarget(Id, East(0), Quaternion.identity, 0);
            motion.SetTarget(Id, East(11), Quaternion.identity, 1);
            var shown = Shown(motion, 1.5f);
            motion.SetTarget(Id, East(30), Quaternion.identity, 1.5f);
            Near(shown, Shown(motion, 1.5f));
        }

        [Test]
        public void TheGlideLearnsTheSnapshotRateAndIgnoresPauses()
        {
            var motion = new ShuttleMotion();
            var now = 0f;
            for (var tick = 0L; tick < 30; tick++, now += 0.1f) motion.BeginSnapshot(tick, now);
            Assert.AreEqual(0.1f, motion.Interval, 0.005f);
            motion.BeginSnapshot(100, now + 60);
            Assert.AreEqual(0.1f, motion.Interval, 0.005f, "a pause is not a slow snapshot rate");
        }

        [Test]
        public void ADiscontinuityOrAnImpossibleMoveJumps()
        {
            var motion = new ShuttleMotion();
            motion.SetTarget(Id, East(0), Quaternion.identity, 0);
            motion.SetTarget(Id, East(10), Quaternion.identity, 1, null, true);
            Near(East(10), Shown(motion, 1));
            motion.SetTarget(Id, East(500), Quaternion.identity, 2);
            Near(East(500), Shown(motion, 2));
        }

        [Test]
        public void ClearingPlacesTheNextPoseAtOnce()
        {
            var motion = new ShuttleMotion();
            motion.SetTarget(Id, East(0), Quaternion.identity, 0);
            motion.Clear();
            Assert.IsFalse(motion.Sample(Id, 0, out _, out _));
            motion.SetTarget(Id, East(100), Quaternion.identity, 1);
            Near(East(100), Shown(motion, 1));
        }

        [Test]
        public void TurningTakesTheShortWayRound()
        {
            var motion = new ShuttleMotion();
            motion.SetTarget(Id, East(0), Quaternion.Euler(0, 350, 0), 0);
            motion.SetTarget(Id, East(11), Quaternion.Euler(0, 10, 0), 1);
            motion.Sample(Id, 1.55f, out _, out var rotation);
            Assert.Less(Quaternion.Angle(Quaternion.identity, rotation), 0.01f);
        }

        // An L-shaped road: east for 10 m, then north for 10 m.
        private static readonly List<Vector2> Bend = new List<Vector2> { new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10) };

        [Test]
        public void AShuttleStaysOnTheRoadAroundABend()
        {
            var motion = new ShuttleMotion();
            motion.SetTarget(Id, new Vector3(5, 0, 0), Quaternion.identity, 0, Bend);
            motion.SetTarget(Id, new Vector3(10, 0, 5), Quaternion.identity, 1, Bend);
            // 10 m of road over 1.1 s, so it reaches the corner halfway, not the straight line's middle
            Near(new Vector3(10, 0, 0), Shown(motion, 1.55f));
            Near(new Vector3(7.5f, 0, 0), Shown(motion, 1.275f));
            Near(new Vector3(10, 0, 2.5f), Shown(motion, 1.825f));
        }

        [Test]
        public void TheRouteIsFollowedInEitherDirection()
        {
            var path = ShuttleMotion.PathBetween(Bend, new Vector3(10, 0, 5), new Vector3(5, 0, 0));
            CollectionAssert.AreEqual(new[] { new Vector3(10, 0, 5), new Vector3(10, 0, 0), new Vector3(5, 0, 0) }, path);
        }

        [Test]
        public void TheHeightChangesEvenlyAroundABend()
        {
            var path = ShuttleMotion.PathBetween(Bend, new Vector3(5, 2, 0), new Vector3(10, 4, 5));
            Assert.AreEqual(3, path.Length);
            Assert.AreEqual(3, path[1].y, 1e-4f);
        }

        [Test]
        public void AMoveTheRouteDoesNotExplainIsAStraightLine()
        {
            var from = new Vector3(5, 0, 0); var to = new Vector3(10, 0, 5);
            Assert.AreEqual(2, ShuttleMotion.PathBetween(null, from, to).Length, "no route");
            Assert.AreEqual(2, ShuttleMotion.PathBetween(Bend, new Vector3(5, 0, 50), to).Length, "off the route");
            Assert.AreEqual(2, ShuttleMotion.PathBetween(Bend, new Vector3(1, 0, 0), new Vector3(9, 0, 0)).Length, "the same segment");
            // A long loop whose ends nearly touch: the near neighbour is not 100 m of driving away
            var loop = new List<Vector2> { new Vector2(0, 0), new Vector2(50, 0), new Vector2(50, 1), new Vector2(0, 1) };
            Assert.AreEqual(2, ShuttleMotion.PathBetween(loop, new Vector3(5, 0, 0), new Vector3(5, 0, 1)).Length, "the wrong part of the route");
        }
    }
}
