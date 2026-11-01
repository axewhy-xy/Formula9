using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Formula9.Traffic.Tests
{
    public class TrafficSpacingTests
    {
        private static readonly TrafficSpacing Solver = new TrafficSpacing();

        private static SpacingFrame Frame(float dt, IList<CarState> cars,
            IList<ShuttleState> shuttles = null, IList<JunctionState> junctions = null)
        {
            return new SpacingFrame(dt, cars,
                shuttles ?? new List<ShuttleState>(),
                junctions ?? new List<JunctionState>());
        }

        private static JunctionState Crossing(bool blocked, SignalState a, SignalState b)
        {
            return new JunctionState("central", blocked, new List<JunctionApproach>
            {
                new JunctionApproach("lane-a", 100f, 115f, a),
                new JunctionApproach("lane-b", 100f, 115f, b)
            });
        }

        [Test]
        public void FollowingDistance_UsesTwoSecondHeadwayPlusStandstillGap()
        {
            var cars = new List<CarState>
            {
                new CarState("behind", "lane-a", 0f, 12f, 4f, 18f),
                new CarState("ahead", "lane-a", 14f, 12f, 4f, 18f)
            };
            var speeds = Solver.CalculateAllowedSpeeds(Frame(1f, cars));
            // Clear bumper gap = 14 - 2 - 2 = 10m.
            // Allowed speed = (10 - 4.5m standstill gap) / 2s.
            Assert.That(speeds["behind"], Is.EqualTo(2.75f).Within(0.001f));
            Assert.That(speeds["ahead"], Is.EqualTo(18f).Within(0.001f));
        }

        [Test]
        public void ShuttleIsNeverMoved_AndCarStopsBeforeIt()
        {
            var cars = new List<CarState>
            {
                new CarState("car", "lane-a", 0f, 10f, 4f, 20f)
            };
            var shuttles = new List<ShuttleState>
            {
                new ShuttleState("backend-shuttle", "lane-a", 10f, 2f, 8f)
            };
            var speeds = Solver.CalculateAllowedSpeeds(Frame(0.5f, cars, shuttles));
            Assert.That(speeds["car"], Is.EqualTo(0f));
            Assert.That(shuttles[0].PositionMeters, Is.EqualTo(10f));
        }

        [TestCase(SignalState.Red)]
        [TestCase(SignalState.Yellow)]
        [TestCase(SignalState.Unknown)]
        public void NonGreenLight_StopsCarBeforeLine(SignalState light)
        {
            var cars = new List<CarState>
            {
                new CarState("car", "lane-a", 91f, 10f, 4f, 20f)
            };
            var junctions = new List<JunctionState> { Crossing(false, light, SignalState.Green) };
            float allowed = Solver.CalculateAllowedSpeeds(Frame(1f, cars, null, junctions))["car"];
            float nextFront = 91f + 2f + allowed;
            Assert.That(nextFront, Is.LessThanOrEqualTo(99.5f + 0.0001f));
        }

        [Test]
        public void BlockedJunction_BlocksEntryEvenWhenLightIsGreen()
        {
            var cars = new List<CarState>
            {
                new CarState("car", "lane-a", 97f, 8f, 4f, 12f)
            };
            float allowed = Solver.CalculateAllowedSpeeds(
                Frame(1f, cars, null, new List<JunctionState>
                {
                    Crossing(true, SignalState.Green, SignalState.Green)
                }))["car"];
            Assert.That(allowed, Is.LessThanOrEqualTo(0.5f + 0.0001f));
        }

        [Test]
        public void CrossingRoads_OnlyOneCarMayEnterSharedJunction()
        {
            var cars = new List<CarState>
            {
                new CarState("a", "lane-a", 97f, 8f, 4f, 8f),
                new CarState("b", "lane-b", 97f, 8f, 4f, 8f)
            };
            var junctions = new List<JunctionState> { Crossing(false, SignalState.Green, SignalState.Green) };
            var speeds = Solver.CalculateAllowedSpeeds(Frame(1f, cars, null, junctions));
            Assert.That(speeds["a"], Is.EqualTo(8f)); // stable ID wins tie
            Assert.That(speeds["b"], Is.LessThanOrEqualTo(0.5f + 0.0001f));
            var updated = new List<CarState>
            {
                new CarState("a", "lane-a", 97f + speeds["a"], speeds["a"], 4f, 8f),
                new CarState("b", "lane-b", 97f + speeds["b"], speeds["b"], 4f, 8f)
            };
            Assert.That(Solver.FindOverlaps(Frame(1f, updated, null, junctions)), Is.Empty);
        }

        [Test]
        public void AlreadyInJunction_CanExitEvenIfSignalChangesToRed()
        {
            var cars = new List<CarState>
            {
                new CarState("inside", "lane-a", 105f, 5f, 4f, 10f),
                new CarState("waiting", "lane-b", 97f, 5f, 4f, 10f)
            };
            var speeds = Solver.CalculateAllowedSpeeds(Frame(1f, cars, null,
                new List<JunctionState> { Crossing(true, SignalState.Red, SignalState.Green) }));
            Assert.That(speeds["inside"], Is.EqualTo(10f));
            Assert.That(speeds["waiting"], Is.LessThanOrEqualTo(0.5f + 0.0001f));
        }

        [Test]
        public void ApproachingBackendShuttle_TakesJunctionPriority()
        {
            var cars = new List<CarState>
            {
                new CarState("car", "lane-b", 97f, 5f, 4f, 12f)
            };
            var shuttles = new List<ShuttleState>
            {
                new ShuttleState("shuttle", "lane-a", 95f, 4f, 6f)
            };
            float allowed = Solver.CalculateAllowedSpeeds(Frame(1f, cars, shuttles,
                new List<JunctionState> { Crossing(false, SignalState.Green, SignalState.Green) }))["car"];
            Assert.That(allowed, Is.LessThanOrEqualTo(0.5f + 0.0001f));
        }

        [Test]
        public void ExistingOverlap_IsDetectedNotSilentlyFixed()
        {
            var cars = new List<CarState>
            {
                new CarState("car", "lane-a", 0f, 5f, 4f, 10f)
            };
            var shuttles = new List<ShuttleState>
            {
                new ShuttleState("shuttle", "lane-a", 3f, 0f, 4f)
            };
            var frame = Frame(0.1f, cars, shuttles);
            Assert.That(Solver.FindOverlaps(frame), Is.Not.Empty);
            Assert.Throws<InvalidOperationException>(() => Solver.CalculateAllowedSpeeds(frame));
        }

        [Test]
        public void StressTest_240Cars_1000Frames_NeverOverlap()
        {
            const float dt = 0.15f;
            const int laneCount = 3;
            const int carsPerLane = 80;
            const int frames = 1000;
            var cars = new List<CarState>();
            for (int lane = 0; lane < laneCount; lane++)
                for (int i = 0; i < carsPerLane; i++)
                    cars.Add(new CarState("c" + lane + "-" + i,
                        "lane-" + lane, -25f - 13f * i, 0f, 4.5f, 13f));

            float shuttleS = 95f;
            for (int tick = 0; tick < frames; tick++)
            {
                // Fake backend movement, independent of the traffic solver.
                var shuttles = new List<ShuttleState>
                {
                    new ShuttleState("s0", "lane-0", shuttleS, 3f, 7f)
                };
                bool red = (tick % 220) >= 170;
                bool blocked = (tick % 310) >= 285;
                var junctions = new List<JunctionState>
                {
                    new JunctionState("crossing", blocked, new List<JunctionApproach>
                    {
                        new JunctionApproach("lane-0", 50f, 67f,
                            red ? SignalState.Red : SignalState.Green),
                        new JunctionApproach("lane-1", 50f, 67f,
                            red ? SignalState.Red : SignalState.Green),
                        new JunctionApproach("lane-2", 50f, 67f,
                            red ? SignalState.Red : SignalState.Green)
                    })
                };
                var before = Frame(dt, cars, shuttles, junctions);
                Assert.That(Solver.FindOverlaps(before), Is.Empty, "Start of frame " + tick);
                var allowed = Solver.CalculateAllowedSpeeds(before);
                var nextCars = new List<CarState>();
                foreach (CarState car in cars)
                {
                    float v = allowed[car.Id];
                    nextCars.Add(new CarState(car.Id, car.LaneId,
                        car.PositionMeters + v * dt, v, car.LengthMeters, car.DesiredSpeedMps));
                }
                shuttleS += 3f * dt;
                var nextShuttles = new List<ShuttleState>
                {
                    new ShuttleState("s0", "lane-0", shuttleS, 3f, 7f)
                };
                Assert.That(Solver.FindOverlaps(Frame(dt, nextCars, nextShuttles, junctions)),
                    Is.Empty, "End of frame " + tick);
                cars = nextCars;
            }
        }
    }
}
