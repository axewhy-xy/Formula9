using System.Collections.Generic;
using System.IO;
using System.Linq;
using Formula9.Backend;
using Formula9.World;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Formula9.Tests
{
    public class CityTrafficTests
    {
        private const float Frame = 0.05f;

        private static readonly List<CityTrafficRenderer> Made = new List<CityTrafficRenderer>();

        [TearDown]
        public void DisposeRenderers()
        {
            foreach (var renderer in Made) renderer.Dispose();
            Made.Clear();
        }

        private static CityRoad Road(string id, long from, long to, bool oneway, string roadClass, params (double X, double Y)[] points) => new CityRoad
        {
            Id = id, Class = roadClass, FromNode = from, ToNode = to, Oneway = oneway, Lanes = 1, SpeedKph = 36, Points = points.ToList(),
        };

        private static CityTrafficRenderer Traffic(string model, string road, int direction, float distance, params CityRoad[] roads)
        {
            var data = new CityTrafficData();
            data.Models[model] = new CityTrafficModel { Id = model, LengthM = 4, WidthM = 2, HeightM = 1.5f };
            data.Vehicles.Add(new CityTrafficVehicle { Id = "v", Model = model, Road = road, Direction = direction, DistanceM = distance, SpeedKph = 36, Color = "#ff0000" });
            var traffic = new CityTrafficRenderer();
            Made.Add(traffic);
            traffic.Rebuild(roads, data);
            Assert.AreEqual(1, traffic.VehicleCount);
            return traffic;
        }

        // Several cars at once: (id, road, direction, distance), each with a speed factor from its id.
        private static CityTrafficRenderer Cars(CitySignalData signals, CityRoad[] roads, params (string Id, string Road, float Distance)[] cars)
        {
            var data = new CityTrafficData();
            data.Models["car"] = new CityTrafficModel { Id = "car", LengthM = 4, WidthM = 2, HeightM = 1.5f };
            foreach (var car in cars)
                data.Vehicles.Add(new CityTrafficVehicle { Id = car.Id, Model = "car", Road = car.Road, Direction = 1, DistanceM = car.Distance, SpeedKph = 36, Color = "#ff0000" });
            var traffic = new CityTrafficRenderer();
            Made.Add(traffic);
            traffic.Rebuild(roads, data, signals);
            traffic.SignalClockS = 0;
            traffic.Tick(0);
            return traffic;
        }

        private static Vector3 Position(CityTrafficRenderer traffic, string id)
        {
            Assert.IsTrue(traffic.TryGetPose(id, out var position, out _), id);
            return position;
        }

        // Phase 0 of this light is green for the first 30 s, then yellow for 3, then red until 70.
        private static CitySignalData Light(string road, float stopX, float stopY, float headingDeg, int phase = 0, int offset = 0) => new CitySignalData
        {
            Junctions = new List<CitySignalJunction>
            {
                new CitySignalJunction { Id = "j", OffsetS = offset, Approaches = new List<CitySignalApproach> { new CitySignalApproach { Road = road, StopX = stopX, StopY = stopY, HeadingDeg = headingDeg, Phase = phase } } },
            },
        };

        [TestCase(0, SignalColor.Green)]
        [TestCase(29.9, SignalColor.Green)]
        [TestCase(30, SignalColor.Yellow)]
        [TestCase(32.9, SignalColor.Yellow)]
        [TestCase(33, SignalColor.Red)]
        [TestCase(69.9, SignalColor.Red)]
        [TestCase(70, SignalColor.Green)]
        public void ALightCyclesAsTheBackendSaysItDoes(double time, SignalColor expected)
        {
            Assert.AreEqual(expected, new CitySignalTiming().ColorAt(time, 0, 0));
        }

        [Test]
        public void TwoPhasesOfALightAreNeverBothAllowedToGo()
        {
            var timing = new CitySignalTiming();
            for (var t = 0.0; t < 70; t += 0.1)
                Assert.IsTrue(timing.ColorAt(t, 13, 0) == SignalColor.Red || timing.ColorAt(t, 13, 1) == SignalColor.Red, t.ToString());
        }

        [Test]
        public void TheSignalReplyIsRead()
        {
            var signals = CityClient.ParseSignals("{\"timing\":{\"green_s\":30,\"yellow_s\":3,\"all_red_s\":2,\"phases\":2,\"cycle_s\":70},\"junctions\":[{\"id\":\"j\",\"offset_s\":6,\"approaches\":[{\"road\":\"way/1/1\",\"heading_deg\":90.5,\"stop_x_m\":1.5,\"stop_y_m\":2.5,\"phase\":1}]}]}");
            Assert.AreEqual(6, signals.Junctions[0].OffsetS);
            Assert.AreEqual("way/1/1", signals.Junctions[0].Approaches[0].Road);
            Assert.AreEqual(1, signals.Junctions[0].Approaches[0].Phase);
            Assert.AreEqual(2.5f, signals.Junctions[0].Approaches[0].StopY);
        }

        [Test]
        public void AFasterCarQueuesBehindASlowerOneInsteadOfDrivingThroughIt()
        {
            // A long way behind or close behind, with every speed factor, the two must never touch
            var road = Road("r", 1, 2, true, "primary", (0, 0), (0, 2000));
            for (var seed = 0; seed < 8; seed++)
            {
                var traffic = Cars(null, new[] { road }, ("back" + seed, "r", 10f), ("front" + seed, "r", 25f));
                for (var i = 0; i < 2400; i++)
                {
                    traffic.Tick(Frame);
                    var report = traffic.Report();
                    Assert.AreEqual(0, report.Overlaps);
                    var gap = Position(traffic, "front" + seed).z - Position(traffic, "back" + seed).z;
                    Assert.Greater(gap, 4f, "centres of 4 m cars");
                    if (Position(traffic, "front" + seed).z > 1900) break;
                }
            }
        }

        [Test]
        public void ACarStopsBehindAStandingOneAndGoesWhenItMovesOn()
        {
            // The first waits at a red light, and the second behind it
            var road = Road("r", 1, 2, true, "primary", (0, 0), (0, 1000));
            var traffic = Cars(Light("r", 0, 200, 90, offset: 35), new[] { road }, ("a", "r", 150f), ("b", "r", 130f));
            for (var i = 0; i < 400; i++) traffic.Tick(Frame); // 20 s, all red (35 + 20 = 55)
            var first = Position(traffic, "a").z;
            var second = Position(traffic, "b").z;
            Assert.AreEqual(198, first, 1.5f, "its front at the line");
            Assert.Greater(first - second, 4f);
            Assert.Less(first - second, 10f);
            for (var i = 0; i < 600; i++) traffic.Tick(Frame); // Green at 70
            Assert.Greater(Position(traffic, "b").z, 210);
        }

        [Test]
        public void ACarStopsAtARedLightAndNeverCrossesIt()
        {
            var road = Road("r", 1, 2, true, "primary", (0, 0), (0, 400));
            var traffic = Cars(Light("r", 0, 100, 90, offset: 33), new[] { road }, ("v", "r", 20f));
            for (var i = 0; i < 680; i++) // Red for 37 s of the 70, so until 34 s from now
            {
                traffic.Tick(Frame);
                Assert.Less(Position(traffic, "v").z + 2, 100.01f, "front behind the line");
            }
            Assert.Greater(Position(traffic, "v").z, 95, "it reached the line");
            for (var i = 0; i < 200; i++) traffic.Tick(Frame);
            Assert.Greater(Position(traffic, "v").z, 100, "it went on green");
            Assert.AreEqual(0, traffic.Report().RedLightRuns);
        }

        [Test]
        public void ALightOnlyControlsTheDirectionItFaces()
        {
            var road = Road("r", 1, 2, false, "primary", (0, 0), (0, 400));
            var signals = Light("r", 0, 100, 270, offset: 33); // Traffic heading south stops here
            var traffic = Cars(signals, new[] { road }, ("v", "r", 20f));
            for (var i = 0; i < 300; i++) traffic.Tick(Frame);
            Assert.Greater(Position(traffic, "v").z, 100);
        }

        [Test]
        public void ACarTooCloseToStopCarriesOnThroughAYellow()
        {
            var road = Road("r", 1, 2, true, "primary", (0, 0), (0, 400));
            // The light turns yellow in 5 s, when it is at speed and just short of the line
            var traffic = Cars(Light("r", 0, 100, 90), new[] { road }, ("v", "r", 50f));
            traffic.SignalClockS = 25;
            for (var i = 0; i < 200; i++) traffic.Tick(Frame);
            Assert.Greater(Position(traffic, "v").z, 100);
            Assert.AreEqual(0, traffic.Report().RedLightRuns);
        }

        // Node 5 is where a primary road running north meets a residential one running east.
        private static CityRoad[] Crossroads() => new[]
        {
            Road("main-in", 1, 5, true, "primary", (0, -100), (0, 0)),
            Road("main-out", 5, 2, true, "primary", (0, 0), (0, 300)),
            Road("side-in", 3, 5, true, "residential", (-100, 0), (0, 0)),
            Road("side-out", 5, 4, true, "residential", (0, 0), (300, 0)),
        };

        [Test]
        public void ASideRoadGivesWayToTheMainRoadAtAJunction()
        {
            var traffic = Cars(null, Crossroads(), ("main", "main-in", 60f), ("side", "side-in", 60f));
            var mainWasFirst = false;
            var sideWent = false;
            var nearest = float.MaxValue;
            for (var i = 0; i < 800; i++)
            {
                traffic.Tick(Frame);
                var main = Position(traffic, "main");
                var side = Position(traffic, "side");
                nearest = Mathf.Min(nearest, Vector3.Distance(main, side));
                // Either may turn at the junction, so through it means north or east of it
                if (main.x + main.z > 5 && side.x + side.z < 0) mainWasFirst = true;
                sideWent |= side.x + side.z > 5;
            }
            Assert.IsTrue(mainWasFirst, "the side road's car waited for the main road's");
            Assert.Greater(nearest, 4, "they never met");
            Assert.IsTrue(sideWent, "the side road's car went after it");
        }

        [Test]
        public void ASideRoadGetsOutOfAMainRoadThatNeverEnds()
        {
            var cars = new List<(string, string, float)> { ("side", "side-in", 90f) };
            for (var i = 0; i < 10; i++) cars.Add(("main" + i, "main-in", 5f + i * 9));
            var traffic = Cars(null, Crossroads(), cars.ToArray());
            for (var i = 0; i < 1500; i++) traffic.Tick(Frame);
            Assert.Greater(Position(traffic, "side").x + Position(traffic, "side").z, 5);
        }

        [Test]
        public void CarsThatGiveWayAtAJunctionDoNotHoldUpOncomingOnes()
        {
            // Both go straight on, on either side of a two-way road: neither has to wait
            var roads = new[]
            {
                Road("south", 1, 5, false, "primary", (0, -100), (0, 0)),
                Road("north", 5, 2, false, "primary", (0, 0), (0, 100)),
            };
            var data = new CityTrafficData();
            data.Models["car"] = new CityTrafficModel { Id = "car", LengthM = 4, WidthM = 2, HeightM = 1.5f };
            data.Vehicles.Add(new CityTrafficVehicle { Id = "up", Model = "car", Road = "south", Direction = 1, DistanceM = 80, SpeedKph = 36, Color = "#fff" });
            data.Vehicles.Add(new CityTrafficVehicle { Id = "down", Model = "car", Road = "north", Direction = -1, DistanceM = 80, SpeedKph = 36, Color = "#fff" });
            var traffic = new CityTrafficRenderer();
            Made.Add(traffic);
            traffic.Rebuild(roads, data);
            traffic.Tick(0);
            for (var i = 0; i < 100; i++) traffic.Tick(Frame);
            Assert.Greater(Position(traffic, "up").z, 5, "past the junction in 5 s");
            Assert.Less(Position(traffic, "down").z, -5, "past the junction in 5 s");
        }

        [Test]
        public void TwoCarsLeavingOneLaneOntoOneRoadDoNotShareIt()
        {
            var roads = new[]
            {
                new CityRoad { Id = "wide", Class = "primary", FromNode = 1, ToNode = 2, Oneway = true, Lanes = 2, SpeedKph = 36, Points = new List<(double X, double Y)> { (0, 0), (0, 60) } },
                Road("narrow", 2, 3, true, "primary", (0, 60), (0, 500)),
            };
            var data = new CityTrafficData();
            data.Models["car"] = new CityTrafficModel { Id = "car", LengthM = 4, WidthM = 2, HeightM = 1.5f };
            data.Vehicles.Add(new CityTrafficVehicle { Id = "a", Model = "car", Road = "wide", Direction = 1, Lane = 0, DistanceM = 40, SpeedKph = 36, Color = "#fff" });
            data.Vehicles.Add(new CityTrafficVehicle { Id = "b", Model = "car", Road = "wide", Direction = 1, Lane = 1, DistanceM = 40, SpeedKph = 36, Color = "#fff" });
            var traffic = new CityTrafficRenderer();
            Made.Add(traffic);
            traffic.Rebuild(roads, data);
            for (var i = 0; i < 600; i++)
            {
                traffic.Tick(Frame);
                Assert.AreEqual(0, traffic.Report().Overlaps);
            }
        }

        private static TrafficHazard Hazard(bool bothWays, params float[] xy)
        {
            var path = new Vector2[xy.Length / 2];
            for (var i = 0; i < path.Length; i++) path[i] = new Vector2(xy[i * 2], xy[i * 2 + 1]);
            return new TrafficHazard { Path = path, BothWays = bothWays };
        }

        [Test]
        public void TrafficTurnsAwayFromAClosedRoadAtAJunction()
        {
            // Without the closure straight on is by far the likelier way
            var north = Road("north", 1, 2, true, "primary", (0, 0), (0, 100));
            var ahead = Road("ahead", 2, 3, true, "primary", (0, 100), (0, 400));
            var off = Road("off", 2, 4, true, "residential", (0, 100), (400, 100));
            var traffic = Cars(null, new[] { north, ahead, off }, ("v", "north", 10f));
            traffic.SetHazards(new[] { Hazard(true, 0, 100, 0, 400) });
            for (var i = 0; i < 500; i++)
            {
                traffic.Tick(Frame);
                Assert.Less(Position(traffic, "v").z, 112, "it did not go up the closed road");
            }
            Assert.Greater(Position(traffic, "v").x, 20);
        }

        [Test]
        public void ACarGivesUpAPlanThatAClosureHasCut()
        {
            var north = Road("north", 1, 2, true, "primary", (0, 0), (0, 200));
            var ahead = Road("ahead", 2, 3, true, "primary", (0, 200), (0, 500));
            var off = Road("off", 2, 4, true, "residential", (0, 200), (400, 200));
            var traffic = Cars(null, new[] { north, ahead, off }, ("v", "north", 150f));
            traffic.Tick(Frame);
            traffic.SetHazards(new[] { Hazard(true, 0, 200, 0, 500) });
            for (var i = 0; i < 400; i++)
            {
                traffic.Tick(Frame);
                Assert.Less(Position(traffic, "v").z, 212);
            }
        }

        [Test]
        public void WithNoOtherWayACarStopsAtTheClosureAndAfterAWhileLeaves()
        {
            var entry = Road("entry", 1, 2, true, "primary", (0, 0), (0, 100));
            var closed = Road("closed", 2, 3, true, "primary", (0, 100), (0, 400));
            var traffic = Cars(null, new[] { entry, closed }, ("v", "entry", 50f));
            traffic.SetHazards(new[] { Hazard(true, 0, 100, 0, 400) });
            for (var i = 0; i < 600; i++)
            {
                traffic.Tick(Frame);
                Assert.Less(Position(traffic, "v").z, 100);
            }
            Assert.Greater(Position(traffic, "v").z, 85, "it came up to the barrier");
            // Far beyond any light's wait: it has stood still long enough to be sent back in at the edge
            for (var i = 0; i < 1800; i++) traffic.Tick(Frame);
            Assert.Less(traffic.Report().MeanSpeedMps + Position(traffic, "v").z, 99, "it did not stay at the barrier for ever");
        }

        [Test]
        public void ACarOnARoadThatClosesIsTakenOffIt()
        {
            var entry = Road("entry", 1, 2, true, "primary", (500, 0), (500, 100));
            var closes = Road("closes", 3, 4, true, "primary", (0, 0), (0, 400));
            var traffic = Cars(null, new[] { entry, closes }, ("v", "closes", 200f));
            traffic.SetHazards(new[] { Hazard(true, 0, 0, 0, 400) });
            traffic.Tick(Frame);
            traffic.Tick(Frame);
            // Back in on a road into the area that is still open
            Assert.Greater(Position(traffic, "v").x, 400);
        }

        [Test]
        public void ACarStopsBehindAQueueOnItsOwnSideOfTheRoadOnly()
        {
            var road = Road("r", 1, 2, false, "primary", (0, 0), (0, 500));
            var data = new CityTrafficData();
            data.Models["car"] = new CityTrafficModel { Id = "car", LengthM = 4, WidthM = 2, HeightM = 1.5f };
            data.Vehicles.Add(new CityTrafficVehicle { Id = "towards", Model = "car", Road = "r", Direction = 1, DistanceM = 50, SpeedKph = 36, Color = "#fff" });
            data.Vehicles.Add(new CityTrafficVehicle { Id = "leaving", Model = "car", Road = "r", Direction = -1, DistanceM = 100, SpeedKph = 36, Color = "#fff" });
            var traffic = new CityTrafficRenderer();
            Made.Add(traffic);
            traffic.Rebuild(new[] { road }, data);
            traffic.Tick(0);
            traffic.SetHazards(new[] { Hazard(false, 0, 200, 0, 300) });
            for (var i = 0; i < 400; i++) traffic.Tick(Frame);
            Assert.AreEqual(197.5f, Position(traffic, "towards").z, 1.5f, "stopped behind the queue");
            Assert.Less(Position(traffic, "leaving").z, 300, "the other direction drove on past it");
        }

        [Test]
        public void AHazardOnACrossingRoadDoesNotStopTraffic()
        {
            var road = Road("r", 1, 2, true, "primary", (0, 0), (0, 500));
            var traffic = Cars(null, new[] { road }, ("v", "r", 50f));
            traffic.SetHazards(new[] { Hazard(true, -100, 200, 100, 200) });
            for (var i = 0; i < 500; i++) traffic.Tick(Frame);
            Assert.Greater(Position(traffic, "v").z, 210);
        }

        [Test]
        public void TrafficComesBackOnceAHazardHasGone()
        {
            var road = Road("r", 1, 2, true, "primary", (0, 0), (0, 500));
            var traffic = Cars(null, new[] { road }, ("v", "r", 50f));
            traffic.SetHazards(new[] { Hazard(false, 0, 200, 0, 300) });
            for (var i = 0; i < 300; i++) traffic.Tick(Frame);
            traffic.SetHazards(new TrafficHazard[0]);
            for (var i = 0; i < 300; i++) traffic.Tick(Frame);
            Assert.Greater(Position(traffic, "v").z, 210);
        }

        private static TrafficObstacle Shuttle(float x, float z, float forwardZ) => new TrafficObstacle
        {
            Id = "s", Position = new Vector3(x, 0, z), Forward = new Vector3(0, 0, forwardZ), HalfLength = 6, HalfWidth = 2.5f,
        };

        [Test]
        public void ACarFollowsAShuttleGoingItsWay()
        {
            var road = Road("r", 1, 2, true, "primary", (0, 0), (0, 2000));
            var traffic = Cars(null, new[] { road }, ("v", "r", 10f));
            var z = 60f;
            for (var i = 0; i < 1200; i++)
            {
                z += 4 * Frame;
                traffic.SetShuttles(new[] { Shuttle(0, z, 1) });
                traffic.Tick(Frame);
                Assert.Less(Position(traffic, "v").z + 2, z - 6, "behind the shuttle's back");
            }
            Assert.Greater(Position(traffic, "v").z, z - 30, "and close behind it");
        }

        [Test]
        public void ACarPullsAsideForAShuttleComingTheOtherWay()
        {
            var road = Road("r", 1, 2, false, "primary", (0, 0), (0, 1000));
            var traffic = Cars(null, new[] { road }, ("v", "r", 100f));
            var z = 400f;
            var clearest = float.MaxValue;
            for (var i = 0; i < 1000 && z > 0; i++)
            {
                z -= 8 * Frame;
                traffic.SetShuttles(new[] { Shuttle(0, z, -1) });
                traffic.Tick(Frame);
                var car = Position(traffic, "v");
                if (Mathf.Abs(car.z - z) < 8) clearest = Mathf.Min(clearest, Mathf.Abs(car.x));
            }
            Assert.Less(clearest, float.MaxValue, "they met");
            Assert.Greater(clearest, 3.4f, "wider than half the shuttle and half the car");
        }

        [Test]
        public void ACarGoesAroundAShuttleStandingInTheRoad()
        {
            var road = Road("r", 1, 2, false, "primary", (0, 0), (0, 1000));
            var traffic = Cars(null, new[] { road }, ("v", "r", 100f));
            traffic.SetShuttles(new[] { Shuttle(0, 200, 1) });
            for (var i = 0; i < 1200; i++)
            {
                traffic.SetShuttles(new[] { Shuttle(0, 200, 1) });
                traffic.Tick(Frame);
                var car = Position(traffic, "v");
                if (Mathf.Abs(car.z - 200) < 8) Assert.Greater(Mathf.Abs(car.x), 3.4f, "clear of its side");
            }
            Assert.Greater(Position(traffic, "v").z, 215, "and on past it");
        }

        [Test]
        public void ACarWaitsForAShuttleCrossingAJunction()
        {
            var traffic = Cars(null, Crossroads(), ("v", "side-in", 40f));
            var z = -80f;
            var closest = float.MaxValue;
            var went = false;
            for (var i = 0; i < 800; i++)
            {
                z += 6 * Frame;
                var shuttle = new TrafficObstacle { Id = "s", Position = new Vector3(0, 0, z), Forward = Vector3.forward, HalfLength = 6, HalfWidth = 2.5f };
                traffic.SetShuttles(new[] { shuttle });
                traffic.Tick(Frame);
                var car = Position(traffic, "v");
                var along = Mathf.Abs(car.z - z);
                if (along < 8) closest = Mathf.Min(closest, Mathf.Abs(car.x));
                went |= car.x + car.z > 5;
            }
            Assert.Greater(closest, 4, "it was never beside the shuttle's length while the shuttle crossed its way");
            Assert.IsTrue(went, "it went on when the shuttle had gone");
        }

        // A shuttle the traffic drives: the bus model's body, on a route north along x = 0.
        private const float BusReach = 6.6f, BusHalfWidth = 2.3f;
        private static readonly List<Vector2> North = new List<Vector2> { new Vector2(0, 0), new Vector2(0, 1000) };

        // A snapshot of the shuttle: where the backend says it is, and how fast it is driving.
        private static bool Snapshot(CityTrafficRenderer traffic, float z, float mps, List<Vector2> route = null) =>
            traffic.DriveShuttle("s", "route", route ?? North, new Vector3(0, 2.5f, z), Quaternion.identity, mps, BusReach, BusHalfWidth);

        private static Vector3 Bus(CityTrafficRenderer traffic)
        {
            Assert.IsTrue(traffic.TryGetShuttlePose("s", out var position, out _));
            return position;
        }

        [Test]
        public void TwoShuttlesQueueAtARedLightWithoutOverlapping()
        {
            // Both backend poses drive on through the light, as the backend did before it knew of the lights,
            // 15 m apart, the backend's headway on one route
            var road = Road("r", 1, 2, true, "primary", (0, 0), (0, 1000));
            var traffic = Cars(Light("r", 0, 200, 90, offset: 35), new[] { road });
            var z = 120f;
            for (var i = 0; i < 400; i++) // 20 s, all red
            {
                z += 10 * Frame;
                traffic.DriveShuttle("a", "route", North, new Vector3(0, 2.5f, z), Quaternion.identity, 10, BusReach, BusHalfWidth);
                traffic.DriveShuttle("b", "route", North, new Vector3(0, 2.5f, z - 15), Quaternion.identity, 10, BusReach, BusHalfWidth);
                traffic.Tick(Frame);
                Assert.AreEqual(0, traffic.Report().ShuttleOverlaps, "the shuttles never touched");
            }
            Assert.IsTrue(traffic.TryGetShuttlePose("a", out var first, out _));
            Assert.IsTrue(traffic.TryGetShuttlePose("b", out var second, out _));
            Assert.AreEqual(200 - BusReach, first.z, 1.5f, "the first at the line");
            Assert.Greater(first.z - second.z, 2 * BusReach, "the second behind its back");
            Assert.Less(first.z - second.z, 2 * BusReach + 4, "and close behind it");
        }

        [Test]
        public void ShuttlesCrossingEachOthersWayDoNotMeet()
        {
            var east = new List<Vector2> { new Vector2(-100, 0), new Vector2(300, 0) };
            var north = new List<Vector2> { new Vector2(0, -100), new Vector2(0, 300) };
            var traffic = Cars(null, Crossroads());
            for (var i = 0; i < 600; i++)
            {
                var travelled = Mathf.Min(-60 + i * 10 * Frame, 250);
                traffic.DriveShuttle("a", "east", east, new Vector3(travelled, 2.5f, 0), Quaternion.LookRotation(Vector3.right), 10, BusReach, BusHalfWidth);
                traffic.DriveShuttle("b", "north", north, new Vector3(0, 2.5f, travelled), Quaternion.identity, 10, BusReach, BusHalfWidth);
                traffic.Tick(Frame);
                Assert.AreEqual(0, traffic.Report().ShuttleOverlaps, "they never met");
            }
            Assert.IsTrue(traffic.TryGetShuttlePose("a", out var a, out _));
            Assert.IsTrue(traffic.TryGetShuttlePose("b", out var b, out _));
            Assert.Greater(a.x, 100, "one went first");
            Assert.Greater(b.z, 100, "and the other after it");
        }

        [Test]
        public void TheLightsKeepToTheSessionsClock()
        {
            var traffic = Cars(null, new[] { Road("r", 1, 2, true, "primary", (0, 0), (0, 1000)) });
            traffic.FollowSignalClock(120);
            Assert.AreEqual(120, traffic.SignalClockS, 1e-9, "set to it when far off");
            traffic.FollowSignalClock(120.3);
            Assert.AreEqual(120, traffic.SignalClockS, 1e-9, "a snapshot a little late or early leaves it running smoothly");
        }

        [Test]
        public void AShuttleWaitsAtTheSameRedLightAsTheBackendAndStaysCloseBehindIt()
        {
            // The backend holds its shuttle with its front 0.5 m short of the line, as the traffic does
            var road = Road("r", 1, 2, true, "primary", (0, 0), (0, 1000));
            var traffic = Cars(Light("r", 0, 200, 90, offset: 25), new[] { road }); // Yellow in 5 s, then red for 37
            var timing = new CitySignalTiming();
            var z = 100f;
            var furthest = 0f;
            for (var i = 0; i < 1600; i++)
            {
                var next = z + 10 * Frame;
                var frontToLine = 200 - z - BusReach;
                var color = timing.ColorAt(traffic.SignalClockS, 25, 0);
                if (frontToLine >= 0 && (color == SignalColor.Red || (color == SignalColor.Yellow && 100f / 12 <= frontToLine)))
                    next = Mathf.Min(next, Mathf.Max(z, 200 - BusReach - 0.5f));
                var moving = next > z;
                z = next;
                Snapshot(traffic, z, moving ? 10 : 0);
                traffic.Tick(Frame);
                furthest = Mathf.Max(furthest, z - Bus(traffic).z);
            }
            Assert.Greater(z, 400, "both went on at green");
            // The backend is at 10 m/s at once; the bus takes 10² / (2 × 1.5) m to get there, on a road no faster
            Assert.Less(furthest, 10 * 10 / (2 * 1.5f) + 1, "it was only ever behind by pulling away more gently than the backend");
            Assert.AreEqual(0, traffic.Report().ShuttleRedLightRuns);
        }

        [Test]
        public void AShuttleNeverGetsAheadOfTheBackend()
        {
            var traffic = Cars(null, new[] { Road("r", 1, 2, true, "primary", (0, 0), (0, 1000)) });
            var z = 100f;
            Assert.IsTrue(Snapshot(traffic, z, 10));
            for (var i = 0; i < 600; i++)
            {
                var moving = i < 300;
                if (moving) z += 10 * Frame;
                Snapshot(traffic, z, moving ? 10 : 0);
                traffic.Tick(Frame);
                Assert.LessOrEqual(Bus(traffic).z, z + 1e-3f, "never past the backend");
            }
            Assert.AreEqual(z, Bus(traffic).z, 0.5f, "and there once the backend stopped");
            Assert.AreEqual(2.5f, Bus(traffic).y, 1e-3f, "at the backend's height");
        }

        [Test]
        public void AShuttleOffItsRouteIsLeftToTheCaller()
        {
            var traffic = Cars(null, new[] { Road("r", 1, 2, true, "primary", (0, 0), (0, 1000)) });
            Assert.IsTrue(Snapshot(traffic, 100, 10));
            Assert.IsFalse(traffic.DriveShuttle("s", "route", North, new Vector3(50, 2.5f, 100), Quaternion.identity, 10, BusReach, BusHalfWidth), "50 m off the route");
            Assert.IsFalse(traffic.TryGetShuttlePose("s", out _, out _));
            Assert.IsFalse(traffic.DrivesShuttle("s"));
            // Reversing out of a depot bay, it faces against the way the route runs
            Assert.IsFalse(traffic.DriveShuttle("s", "route", North, new Vector3(0, 2.5f, 100), Quaternion.LookRotation(Vector3.back), 10, BusReach, BusHalfWidth));
        }

        [Test]
        public void AShuttleStopsAtARedLightAndGoesOnGreen()
        {
            var road = Road("r", 1, 2, true, "primary", (0, 0), (0, 1000));
            var traffic = Cars(Light("r", 0, 200, 90, offset: 33), new[] { road }); // Red for 37 s
            var z = 100f;
            for (var i = 0; i < 680; i++)
            {
                z = Mathf.Min(z + 10 * Frame, 600);
                Snapshot(traffic, z, 10);
                traffic.Tick(Frame);
                Assert.Less(Bus(traffic).z + BusReach, 200.01f, "front behind the line");
            }
            Assert.Greater(Bus(traffic).z + BusReach, 195, "it reached the line");
            for (var i = 0; i < 1200; i++)
            {
                z = Mathf.Min(z + 10 * Frame, 600);
                Snapshot(traffic, z, z < 600 ? 10 : 0);
                traffic.Tick(Frame);
            }
            Assert.AreEqual(600, Bus(traffic).z, 0.5f, "it went on green and caught up with the backend");
            Assert.AreEqual(0, traffic.Report().ShuttleRedLightRuns);
        }

        [Test]
        public void AShuttleIgnoresALightFacingTheOtherWay()
        {
            var road = Road("r", 1, 2, false, "primary", (0, 0), (0, 1000));
            var traffic = Cars(Light("r", 0, 200, 270, offset: 33), new[] { road });
            var z = 100f;
            for (var i = 0; i < 400; i++)
            {
                z += 10 * Frame;
                Snapshot(traffic, z, 10);
                traffic.Tick(Frame);
            }
            Assert.Greater(Bus(traffic).z, 250);
        }

        [TestCase(false, 1, -1.6f)]
        [TestCase(false, 2, -4.8f)]
        [TestCase(true, 2, -1.6f)]
        public void AShuttleKeepsToTheKerbLane(bool oneway, int lanes, float expectedX)
        {
            var road = Road("r", 1, 2, oneway, "primary", (0, 0), (0, 1000));
            road.Lanes = lanes;
            var traffic = Cars(null, new[] { road });
            var z = 100f;
            for (var i = 0; i < 100; i++)
            {
                z += 10 * Frame;
                Snapshot(traffic, z, 10);
                traffic.Tick(Frame);
                Assert.AreEqual(expectedX, Bus(traffic).x, 0.05f, "on the left, as Singapore drives");
            }
        }

        [Test]
        public void AShuttleQueuesBehindACarInsteadOfDrivingThroughIt()
        {
            // The car waits at a red light, and the backend, knowing nothing of either, drives on
            var road = Road("r", 1, 2, true, "primary", (0, 0), (0, 1000));
            var traffic = Cars(Light("r", 0, 200, 90, offset: 35), new[] { road }, ("a", "r", 150f));
            var z = 100f;
            for (var i = 0; i < 400; i++) // 20 s, all red
            {
                z += 10 * Frame;
                Snapshot(traffic, z, 10);
                traffic.Tick(Frame);
                Assert.Less(Bus(traffic).z + BusReach, Position(traffic, "a").z - 2, "behind the car's back");
                Assert.AreEqual(0, traffic.Report().ShuttleOverlaps);
            }
            Assert.Greater(Bus(traffic).z + BusReach, Position(traffic, "a").z - 2 - 4, "queued close behind it");
            for (var i = 0; i < 1000; i++)
            {
                z += 10 * Frame;
                Snapshot(traffic, z, 10);
                traffic.Tick(Frame);
                Assert.AreEqual(0, traffic.Report().ShuttleOverlaps);
            }
            Assert.Greater(Bus(traffic).z, 300, "it followed the car on through the junction");
        }

        [Test]
        public void ACarQueuesBehindAShuttleHeldAtARedLight()
        {
            var road = Road("r", 1, 2, true, "primary", (0, 0), (0, 1000));
            var traffic = Cars(Light("r", 0, 200, 90, offset: 35), new[] { road }, ("v", "r", 60f));
            var z = 150f;
            for (var i = 0; i < 400; i++)
            {
                z += 10 * Frame;
                Snapshot(traffic, z, 10);
                traffic.Tick(Frame);
                Assert.AreEqual(0, Position(traffic, "v").x, 0.05f, "it stayed in its lane instead of going round");
                Assert.AreEqual(0, traffic.Report().ShuttleOverlaps);
            }
            Assert.Less(Position(traffic, "v").z + 2, Bus(traffic).z - 6, "behind the shuttle");
        }

        [Test]
        public void AShuttleStopsForACarStandingAcrossItsWayAndDoesNotWaitForEver()
        {
            // The car stands across the shuttle's road at z = 50, held up by a queue on its own road
            var road = Road("r", 1, 2, true, "primary", (0, 0), (0, 1000));
            var across = Road("across", 3, 4, true, "primary", (-100, 50), (100, 50));
            var traffic = Cars(null, new[] { road, across }, ("v", "across", 100f));
            traffic.SetHazards(new[] { Hazard(false, 5, 50, 60, 50) });
            var z = 0f;
            for (var i = 0; i < 400; i++) // 20 s
            {
                z += 10 * Frame;
                Snapshot(traffic, z, 10);
                traffic.Tick(Frame);
                Assert.AreEqual(0, traffic.Report().ShuttleOverlaps, "it did not run the car over");
            }
            Assert.Less(Bus(traffic).z + BusReach, 49);
            for (var i = 0; i < 600; i++) // Past the 30 s it waits for a vehicle that never moves
            {
                z += 10 * Frame;
                Snapshot(traffic, z, 10);
                traffic.Tick(Frame);
            }
            Assert.Greater(Bus(traffic).z, 60, "the stuck car left and the shuttle went on past where it stood");
        }

        [Test]
        public void ACarCrossesOnItsGreenWhileAShuttleWaitsAtItsRed()
        {
            // The shuttle comes up main-in from the south; the car comes along side-in from the west
            var route = new List<Vector2> { new Vector2(0, -100), new Vector2(0, 300) };
            var signals = new CitySignalData
            {
                Junctions = new List<CitySignalJunction>
                {
                    new CitySignalJunction
                    {
                        Id = "j", OffsetS = 35, // Phase 0, the main road, red; phase 1, the side road, green
                        Approaches = new List<CitySignalApproach>
                        {
                            new CitySignalApproach { Road = "main-in", StopX = 0, StopY = -8, HeadingDeg = 90, Phase = 0 },
                            new CitySignalApproach { Road = "side-in", StopX = -8, StopY = 0, HeadingDeg = 0, Phase = 1 },
                        },
                    },
                },
            };
            var traffic = Cars(signals, Crossroads(), ("v", "side-in", 40f));
            var z = -60f;
            var carWent = false;
            for (var i = 0; i < 400; i++) // 20 s of the shuttle's red and the car's green
            {
                z += 10 * Frame;
                Snapshot(traffic, z, 10, route);
                traffic.Tick(Frame);
                Assert.Less(Bus(traffic).z + BusReach, -8 + 0.01f, "the shuttle waited at its light");
                Assert.AreEqual(0, traffic.Report().ShuttleOverlaps);
                carWent |= Position(traffic, "v").x + Position(traffic, "v").z > 5;
            }
            Assert.IsTrue(carWent, "the car crossed on its green");
            Assert.AreEqual(0, traffic.Report().ShuttleRedLightRuns);
        }

        // The real roads, vehicles and lights, as the backend serves them, for four minutes of traffic.
        [Test]
        public void TheRealCityKeepsMovingWithNoOverlapsOrRedLightRuns()
        {
            var traffic = RealCity(out _);
            traffic.SignalClockS = 1000;
            traffic.Tick(0);
            var seeded = traffic.Report();
            Assert.Greater(seeded.Driving, 900);
            var slowest = float.MaxValue;
            var mostOverlaps = 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 4800; i++)
            {
                traffic.Tick(Frame);
                if (i % 20 != 0) continue;
                var report = traffic.Report();
                mostOverlaps = Mathf.Max(mostOverlaps, report.Overlaps);
                if (i > 1200) slowest = Mathf.Min(slowest, report.MeanSpeedMps);
            }
            watch.Stop();
            var end = traffic.Report();
            Debug.Log($"Traffic soak: {end.Driving} driving, {end.Outside} outside, slowest mean {slowest:0.0} m/s, end mean {end.MeanSpeedMps:0.0} m/s, most overlaps {mostOverlaps}, {end.RedLightRuns} red runs, {watch.ElapsedMilliseconds / 4800f:0.00} ms a step");
            Assert.AreEqual(0, end.RedLightRuns, "nobody ran a red light");
            Assert.AreEqual(0, mostOverlaps, "nobody drove through the vehicle in front");
            Assert.Greater(slowest, 2f, "traffic never seized up");
            Assert.Less(end.Outside, end.Driving / 10, "the roads in have room");
        }

        [Test]
        public void AShuttleDrivesThroughTheRealCityWithoutTouchingTheTrafficOrRunningARedLight()
        {
            var traffic = RealCity(out var roads);
            traffic.SignalClockS = 1000;
            traffic.Tick(0);
            var route = StraightOn(roads, 2500);
            var length = 0f;
            for (var i = 1; i < route.Count; i++) length += Vector2.Distance(route[i - 1], route[i]);
            Assert.Greater(length, 1000, "a long enough drive");

            var along = 0f;
            var mostOverlaps = 0;
            var held = 0f;
            var arrived = -1;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; arrived < 0 || i < arrived + 1200; i++) // Until 60 s after the backend arrives
            {
                along = Mathf.Min(along + 10 * Frame, length);
                if (along >= length && arrived < 0) arrived = i;
                var at = PointAlong(route, along, out var heading);
                Assert.IsTrue(traffic.DriveShuttle("s", "route", route, new Vector3(at.x, 2.5f, at.y), Quaternion.LookRotation(new Vector3(heading.x, 0, heading.y)), along < length ? 10 : 0, BusReach, BusHalfWidth), "on its route");
                traffic.Tick(Frame);
                var shown = Bus(traffic);
                if (Vector2.Distance(new Vector2(shown.x, shown.z), at) > 20) held += Frame;
                mostOverlaps = Mathf.Max(mostOverlaps, traffic.Report().ShuttleOverlaps);
            }
            watch.Stop();
            var end = traffic.Report();
            var last = Bus(traffic);
            Debug.Log($"Shuttle drive: {length:0} m, {route.Count} points, {held:0.0} s held back by the traffic, most overlaps {mostOverlaps}, {end.ShuttleRedLightRuns} red runs, {watch.ElapsedMilliseconds:0} ms");
            Assert.AreEqual(0, end.ShuttleRedLightRuns, "it never ran a red light");
            Assert.AreEqual(0, mostOverlaps, "it never touched a vehicle");
            Assert.Less(Vector2.Distance(new Vector2(last.x, last.z), route[route.Count - 1]), 10, "it got to the end of its route");
        }

        [Test]
        public void AShuttleDrivesThroughTheRealCityWithABackendThatStopsAtTheLightsToo()
        {
            // The backend now holds its shuttle at a red light, on the session's clock, as the traffic does
            var traffic = RealCity(out var roads);
            traffic.SignalClockS = 1000;
            traffic.Tick(0);
            var route = StraightOn(roads, 2500);
            var length = Length(route);
            var lines = BackendStopLines(route);
            Assert.Greater(lines.Count, 0, "lights on the way");
            var timing = new CitySignalTiming();

            var along = 0f;
            var behind = 0f;
            var furthest = 0f;
            var mostOverlaps = 0;
            for (var i = 0; along < length && i < 12000; i++)
            {
                var next = along + 10 * Frame;
                foreach (var (at, offset, phase) in lines)
                {
                    var frontToLine = at - along - BusReach;
                    if (frontToLine < 0) continue;
                    var color = timing.ColorAt(traffic.SignalClockS, offset, phase);
                    if (color == SignalColor.Red || (color == SignalColor.Yellow && 100f / 12 <= frontToLine))
                        next = Mathf.Min(next, Mathf.Max(along, at - BusReach - 0.5f));
                }
                var moving = next > along;
                along = Mathf.Min(next, length);
                var point = PointAlong(route, along, out var heading);
                traffic.DriveShuttle("s", "route", route, new Vector3(point.x, 2.5f, point.y), Quaternion.LookRotation(new Vector3(heading.x, 0, heading.y)), moving ? 10 : 0, BusReach, BusHalfWidth);
                traffic.Tick(Frame);
                var shown = Bus(traffic);
                var gap = Vector2.Distance(new Vector2(shown.x, shown.z), point);
                furthest = Mathf.Max(furthest, gap);
                if (gap > 20) behind += Frame;
                if (i % 10 == 0) mostOverlaps = Mathf.Max(mostOverlaps, traffic.Report().ShuttleOverlaps);
            }
            Debug.Log($"Shuttle with the backend at the lights: {length:0} m, {lines.Count} lights, {behind:0.0} s more than 20 m behind, furthest {furthest:0} m behind, most overlaps {mostOverlaps}, {traffic.Report().ShuttleRedLightRuns} red runs");
            Assert.AreEqual(0, traffic.Report().ShuttleRedLightRuns);
            Assert.AreEqual(0, mostOverlaps);
            // The backend cannot see the cars, so a queue at a light still holds the shuttle further back
            Assert.Less(furthest, 400, "never so far behind that it was put on the backend's pose");
        }

        // Where the backend's shuttle stops on a route: the lights' stop lines, matched as route_signals.py does.
        private static List<(float At, int Offset, int Phase)> BackendStopLines(List<Vector2> route)
        {
            var signals = CityClient.ParseSignals(File.ReadAllText(Path.Combine(Application.dataPath, "..", "backend", "fixtures", "traffic", "toa-payoh-signals.json")));
            var lines = new List<(float, int, int)>();
            foreach (var junction in signals.Junctions)
                foreach (var approach in junction.Approaches)
                {
                    var heading = approach.HeadingDeg * Mathf.Deg2Rad;
                    var travel = new Vector2(Mathf.Cos(heading), Mathf.Sin(heading));
                    var stop = new Vector2(approach.StopX, approach.StopY);
                    var travelled = 0f;
                    for (var i = 1; i < route.Count; i++)
                    {
                        var run = route[i] - route[i - 1];
                        var step = run.magnitude;
                        if (step <= 0) continue;
                        var share = Mathf.Clamp(Vector2.Dot(stop - route[i - 1], run / step), 0, step);
                        var at = travelled + share;
                        if (Vector2.Distance(stop, route[i - 1] + run / step * share) <= 8 && Vector2.Dot(run / step, travel) >= 0.7f
                            && lines.TrueForAll(l => Mathf.Abs(l.Item1 - at) >= 10)) lines.Add((at, junction.OffsetS, approach.Phase));
                        travelled += step;
                    }
                }
            return lines;
        }

        // The longest route from a main road a light controls, straight on at every junction, up to a distance.
        private static List<Vector2> StraightOn(List<CityRoad> roads, float farM)
        {
            var signals = CityClient.ParseSignals(File.ReadAllText(Path.Combine(Application.dataPath, "..", "backend", "fixtures", "traffic", "toa-payoh-signals.json")));
            var byId = roads.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
            var leaving = new Dictionary<long, List<(CityRoad Road, bool Forward)>>();
            foreach (var road in roads)
            {
                if (road.Class == "service") continue;
                if (!leaving.TryGetValue(road.FromNode, out var from)) leaving[road.FromNode] = from = new List<(CityRoad, bool)>();
                from.Add((road, true));
                if (road.Oneway) continue;
                if (!leaving.TryGetValue(road.ToNode, out var to)) leaving[road.ToNode] = to = new List<(CityRoad, bool)>();
                to.Add((road, false));
            }
            List<Vector2> longest = null;
            var longestM = 0f;
            foreach (var start in signals.Junctions.SelectMany(j => j.Approaches))
            {
                if (!byId.TryGetValue(start.Road, out var road) || (road.Class != "primary" && road.Class != "secondary")) continue;
                var heading = start.HeadingDeg * Mathf.Deg2Rad;
                var forward = Vector2.Dot(Point(road.Points[road.Points.Count - 1]) - Point(road.Points[0]), new Vector2(Mathf.Cos(heading), Mathf.Sin(heading))) >= 0;
                if (!forward && road.Oneway) continue;
                var points = RoadPath(road, forward);
                var node = forward ? road.ToNode : road.FromNode;
                var length = Length(points);
                var used = new HashSet<string> { road.Id };
                while (length < farM && leaving.TryGetValue(node, out var exits))
                {
                    var into = points[points.Count - 1] - points[points.Count - 2];
                    (CityRoad Road, bool Forward) best = default;
                    var bestAngle = 50f;
                    foreach (var exit in exits)
                    {
                        if (used.Contains(exit.Road.Id)) continue;
                        var path = RoadPath(exit.Road, exit.Forward);
                        var angle = Vector2.Angle(into, path[1] - path[0]);
                        if (angle < bestAngle) { bestAngle = angle; best = exit; }
                    }
                    if (best.Road == null) break;
                    used.Add(best.Road.Id);
                    node = best.Forward ? best.Road.ToNode : best.Road.FromNode;
                    var next = RoadPath(best.Road, best.Forward);
                    for (var i = 1; i < next.Count; i++) { length += Vector2.Distance(points[points.Count - 1], next[i]); points.Add(next[i]); }
                }
                if (length > longestM) { longestM = length; longest = points; }
                if (longestM >= farM) break;
            }
            return longest;
        }

        private static Vector2 Point((double X, double Y) point) => new Vector2((float)point.X, (float)point.Y);

        private static List<Vector2> RoadPath(CityRoad road, bool forward)
        {
            var path = road.Points.Select(Point).ToList();
            if (!forward) path.Reverse();
            return path;
        }

        private static float Length(List<Vector2> line)
        {
            var length = 0f;
            for (var i = 1; i < line.Count; i++) length += Vector2.Distance(line[i - 1], line[i]);
            return length;
        }

        private static Vector2 PointAlong(List<Vector2> line, float along, out Vector2 heading)
        {
            heading = Vector2.up;
            for (var i = 1; i < line.Count; i++)
            {
                var step = Vector2.Distance(line[i - 1], line[i]);
                if (step <= 0) continue;
                heading = (line[i] - line[i - 1]) / step;
                if (along <= step || i == line.Count - 1) return Vector2.Lerp(line[i - 1], line[i], Mathf.Clamp01(along / step));
                along -= step;
            }
            return line[line.Count - 1];
        }

        // The real roads, vehicles and lights of Toa Payoh, as the backend serves them.
        private static CityTrafficRenderer RealCity(out List<CityRoad> roads)
        {
            var folder = Path.Combine(Application.dataPath, "..", "backend", "fixtures", "traffic");
            var root = JObject.Parse(File.ReadAllText(Path.Combine(folder, "toa-payoh.json")));
            roads = root["roads"].Select(r => new CityRoad
            {
                Id = (string)r["id"], Class = (string)r["class"], FromNode = (long)r["from_node"], ToNode = (long)r["to_node"], Oneway = (bool)r["oneway"],
                Lanes = (int)r["lanes"], SpeedKph = (int?)r["speed_kph"] ?? 40, Points = r["points_m"].Select(p => ((double)p[0], (double)p[1])).ToList(),
            }).ToList();
            var data = new CityTrafficData();
            foreach (var model in ((JObject)root["models"]).Properties())
                data.Models[model.Name] = new CityTrafficModel { Id = model.Name, LengthM = (float)model.Value["length_m"], WidthM = (float)model.Value["width_m"], HeightM = (float)model.Value["height_m"] };
            data.Vehicles = root["vehicles"].Select(v => new CityTrafficVehicle
            {
                Id = (string)v["id"], Model = (string)v["model"], Color = (string)v["color"], Road = (string)v["road"], Direction = (int)v["direction"], Lane = (int)v["lane"], DistanceM = (float)v["distance_m"], SpeedKph = (int)v["speed_kph"],
            }).ToList();
            var signals = CityClient.ParseSignals(File.ReadAllText(Path.Combine(folder, "toa-payoh-signals.json")));

            var traffic = new CityTrafficRenderer();
            Made.Add(traffic);
            traffic.Rebuild(roads, data, signals);
            return traffic;
        }

        private static Vector3 Position(CityTrafficRenderer traffic)
        {
            Assert.IsTrue(traffic.TryGetPose("v", out var position, out _));
            return position;
        }

        // The backend's point_along puts a lane to the left of travel; the seed's x_m and y_m come from it.
        [TestCase(1, -1.6f)]
        [TestCase(-1, 1.6f)]
        public void ATwoWayRoadsVehiclesKeepToTheLeft(int direction, float expectedX)
        {
            var traffic = Traffic("car", "r", direction, 50, Road("r", 1, 2, false, "primary", (0, 0), (0, 100)));
            traffic.Tick(0);
            Assert.AreEqual(expectedX, Position(traffic).x, 1e-3f);
            Assert.AreEqual(50, Position(traffic).z, 1e-3f);
        }

        [Test]
        public void ADeadEndSendsTheVehicleBackInAtTheEdgeOfTheArea()
        {
            var entry = Road("entry", 3, 1, true, "primary", (500, 0), (500, 100));
            var deadEnd = Road("dead", 1, 2, true, "primary", (0, 0), (0, 100));
            var traffic = Traffic("car", "dead", 1, 99, entry, deadEnd);
            traffic.Tick(0);
            for (var i = 0; i < 40; i++) traffic.Tick(Frame);
            Assert.Greater(Position(traffic).x, 400, "it left the dead end for the road coming into the area");
        }

        [Test]
        public void ALargeVehicleTurnsBackInsteadOfTakingAServiceRoad()
        {
            var main = Road("main", 1, 2, false, "primary", (0, 0), (0, 40));
            var service = Road("yard", 2, 3, false, "service", (0, 40), (200, 40));
            var traffic = Traffic("bus", "main", 1, 30, main, service);
            traffic.Tick(0);
            for (var i = 0; i < 300; i++)
            {
                traffic.Tick(Frame);
                Assert.Less(Position(traffic).x, 20, "still on the main road");
            }
        }

        [Test]
        public void ACarMayTakeAServiceRoad()
        {
            var main = Road("main", 1, 2, false, "primary", (0, 0), (0, 40));
            var service = Road("yard", 2, 3, false, "service", (0, 40), (200, 40));
            var traffic = Traffic("car", "main", 1, 30, main, service);
            traffic.Tick(0);
            for (var i = 0; i < 300; i++) traffic.Tick(Frame);
            Assert.Greater(Position(traffic).x, 20);
        }

        [Test]
        public void ATurnIsSteeredNotSnapped()
        {
            var north = Road("north", 1, 2, true, "primary", (0, 0), (0, 30));
            var east = Road("east", 2, 3, true, "primary", (0, 30), (200, 30));
            var traffic = Traffic("car", "north", 1, 20, north, east);
            traffic.Tick(0);
            Assert.IsTrue(traffic.TryGetPose("v", out var lastPosition, out var lastForward));
            var turned = false;
            for (var i = 0; i < 200; i++)
            {
                traffic.Tick(Frame);
                Assert.IsTrue(traffic.TryGetPose("v", out var position, out var forward));
                Assert.Less(Vector3.Angle(lastForward, forward), 25f, "a frame's change of heading");
                Assert.Less(Vector3.Distance(lastPosition, position), 1.5f, "a frame's change of place");
                turned |= Vector3.Angle(forward, Vector3.right) < 1f;
                lastPosition = position;
                lastForward = forward;
            }
            Assert.IsTrue(turned, "it ended up heading along the second road");
        }

        [Test]
        public void ADeltaOfZeroHoldsTrafficStill()
        {
            var traffic = Traffic("car", "r", 1, 50, Road("r", 1, 2, true, "primary", (0, 0), (0, 100)));
            traffic.Tick(0);
            var before = Position(traffic);
            traffic.Tick(0);
            Assert.AreEqual(before, Position(traffic));
        }

        [Test]
        public void ALongFrameMovesAVehicleOnlyAShortWay()
        {
            var traffic = Traffic("car", "r", 1, 50, Road("r", 1, 2, true, "primary", (0, 0), (0, 500)));
            traffic.Tick(0);
            var before = Position(traffic);
            traffic.Tick(30);
            Assert.Less(Vector3.Distance(before, Position(traffic)), 3);
        }

        [Test]
        public void AModelAuthoredAlongXIsTurnedToFaceAlongTheRoad()
        {
            var model = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                model.transform.localScale = new Vector3(4, 1, 2);
                var bounds = CityTrafficRenderer.ModelBounds(model, 0, 8);
                Assert.AreEqual(8, bounds.size.z, 1e-3f, "the long side runs along the road, at the model's length");
                Assert.AreEqual(4, bounds.size.x, 1e-3f, "scaled evenly");
                Assert.AreEqual(0, bounds.min.y, 1e-3f, "standing on the road");
                Assert.AreEqual(0, bounds.center.z, 1e-3f, "centred on its place");
            }
            finally { Object.DestroyImmediate(model); }
        }

        [Test]
        public void EveryCatalogPrefabIsDrawableAndFacesForward()
        {
            var catalog = Resources.Load<TrafficVehicleCatalog>("Formula9/TrafficVehicleCatalog");
            Assert.IsNotNull(catalog);
            Assert.IsNotEmpty(catalog.entries);
            foreach (var entry in catalog.entries)
            {
                Assert.IsNotNull(entry.prefab, entry.modelId);
                var renderers = entry.prefab.GetComponentsInChildren<Renderer>(true);
                Assert.IsNotEmpty(renderers, entry.prefab.name);
                foreach (var renderer in renderers)
                    Assert.IsFalse(renderer.sharedMaterials.Any(m => m == null), entry.prefab.name + " has a missing material on " + renderer.name);
                var bounds = CityTrafficRenderer.ModelBounds(entry.prefab, entry.yawDegrees, 5);
                Assert.AreEqual(5, bounds.size.z, 1e-2f, entry.prefab.name);
                Assert.Less(bounds.size.x, bounds.size.z, entry.prefab.name);
            }
        }

        [Test]
        public void AMotorcycleIsNotDrawnAsACar()
        {
            var catalog = Resources.Load<TrafficVehicleCatalog>("Formula9/TrafficVehicleCatalog");
            var cars = catalog.entries.Where(e => e.modelId == "car").Select(e => e.prefab).ToList();
            Assert.IsFalse(catalog.entries.Any(e => e.modelId == "motorcycle" && cars.Contains(e.prefab)));
        }

        [Test]
        public void ThePaintedPacksHaveNoMissingReferences()
        {
            var missing = new List<string>();
            foreach (var pack in new[] { "Assets/Cobra Games Studio", "Assets/Low poly vehicle pack lite", "Assets/Super Retro Compact Pack", "Assets/Ukraine_only_truck" })
                foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { pack }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (AssetImporter.GetAtPath(path) is ModelImporter importer)
                        foreach (var remap in importer.GetExternalObjectMap().Where(pair => pair.Value == null))
                            missing.Add(path + ": " + remap.Key.name);
                }
            Assert.IsEmpty(missing);
        }

        [Test]
        public void TheVehicleMaterialIsSavedWithInstancingOnForPlayerBuilds()
        {
            var material = Resources.Load<Material>("Formula9/TrafficVehicle");
            Assert.IsNotNull(material);
            Assert.AreEqual("Formula9/TrafficVehicle", material.shader.name);
            Assert.IsTrue(material.enableInstancing);
        }

        // ---------------------------------------------------------------- following the backend

        // A frame of the backend's traffic: the sample's first vehicle at (x, y) heading east, or none,
        // and the shuttle "shuttle-01" heading north at (shuttleX, shuttleY).
        private static TrafficFrame BackendFrame(long tick, float? x, float y = 0, float speed = 10, float shuttleX = 50, float shuttleY = 0) => new TrafficFrame
        {
            Tick = tick,
            SimulationTimeS = tick * 0.05,
            Vehicles = x.HasValue ? new[] { 0, Mathf.RoundToInt(x.Value * 100), Mathf.RoundToInt(y * 100), 0, Mathf.RoundToInt(speed * 100) } : new int[0],
            Shuttles = new List<TrafficShuttlePose> { new TrafficShuttlePose { VehicleId = "shuttle-01", XM = shuttleX, YM = shuttleY, HeadingDeg = 90, SpeedMps = 10 } },
        };

        private static CityTrafficRenderer FollowingTheBackend()
        {
            var traffic = Traffic("car", "r", 1, 10, Road("r", 1, 2, true, "primary", (0, 0), (2000, 0)));
            traffic.FollowBackend(new[] { BackendFrame(200, 100), BackendFrame(202, 101) });
            Assert.IsTrue(traffic.FollowsBackend);
            return traffic;
        }

        [Test]
        public void TheBackendsFramesPlaceTheTrafficAndNothingElseMovesIt()
        {
            var traffic = FollowingTheBackend();
            traffic.Tick(0);
            Assert.AreEqual(100, Position(traffic, "v").x, 0.01f, "drawn from the frames, not where the sample put it");
            Assert.AreEqual(0, Position(traffic, "v").z, 0.01f);
            for (var i = 0; i < 100; i++)
            {
                traffic.Tick(Frame);
                var x = Position(traffic, "v").x;
                Assert.GreaterOrEqual(x, 100 - 0.01f);
                Assert.LessOrEqual(x, 101 + 0.01f, "never past the newest frame, whatever the time");
            }
        }

        [Test]
        public void TheTrafficIsDrawnBetweenTheFramesEitherSideOfItsClock()
        {
            var traffic = Traffic("car", "r", 1, 10, Road("r", 1, 2, true, "primary", (0, 0), (2000, 0)));
            // 10 m/s east, from x = 98 at 9.8 s to x = 101 at 10.1 s
            traffic.FollowBackend(Enumerable.Range(0, 4).Select(k => BackendFrame(196 + 2 * k, 98 + k)).ToList());
            traffic.Tick(0);
            Assert.AreEqual(10.1 - 0.1 - TrafficPlaybackClock.StartMarginS, traffic.FollowedS, 1e-9, "a frame and the margin behind the newest");
            for (var i = 0; i < 3; i++)
            {
                var before = traffic.FollowedS;
                traffic.Tick(Frame);
                Assert.Greater(traffic.FollowedS, before);
                Assert.Less(traffic.FollowedS, 10.1);
                Assert.AreEqual(98 + (traffic.FollowedS - 9.8) * 10, Position(traffic, "v").x, 0.01f);
                Assert.AreEqual(traffic.FollowedS, traffic.SignalClockS, 1e-9, "the lights show the colours the backend stopped for");
            }
        }

        [Test]
        public void AVehicleTheBackendHasOffTheMapIsNotDrawn()
        {
            var traffic = Traffic("car", "r", 1, 10, Road("r", 1, 2, true, "primary", (0, 0), (2000, 0)));
            traffic.FollowBackend(new[] { BackendFrame(200, null), BackendFrame(202, null) });
            traffic.Tick(Frame);
            Assert.IsFalse(traffic.TryGetPose("v", out _, out _));
        }

        [Test]
        public void TheBackendsShuttleIsDrawnWhereItsFramesPutIt()
        {
            var traffic = Traffic("car", "r", 1, 10, Road("r", 1, 2, true, "primary", (0, 0), (2000, 0)));
            // 5 m/s north, from y = 0 at 9.8 s
            traffic.FollowBackend(Enumerable.Range(0, 4).Select(k => BackendFrame(196 + 2 * k, 98 + k, shuttleY: 0.5f * k)).ToList());
            Assert.IsFalse(traffic.TryGetBackendShuttlePose("shuttle-01", 1.6f, out _, out _), "nothing drawn yet");
            traffic.Tick(0);
            traffic.Tick(Frame);
            Assert.IsTrue(traffic.TryGetBackendShuttlePose("shuttle-01", 1.6f, out var at, out var facing));
            Assert.AreEqual(50, at.x, 0.01f);
            Assert.AreEqual((traffic.FollowedS - 9.8) * 5, at.z, 0.01f);
            Assert.AreEqual(1.6f, at.y, 0.001f);
            Assert.Greater(Vector3.Dot(facing * Vector3.forward, Vector3.forward), 0.999f, "north, along +Z");
            Assert.IsFalse(traffic.TryGetBackendShuttlePose("shuttle-02", 1.6f, out _, out _));
        }

        [Test]
        public void ForgettingTheBackendLetsTheTrafficDriveItselfAgain()
        {
            var traffic = FollowingTheBackend();
            traffic.Tick(Frame);
            traffic.ForgetBackend();
            Assert.IsFalse(traffic.FollowsBackend);
            traffic.Tick(Frame);
            var start = Position(traffic, "v").x;
            for (var i = 0; i < 40; i++) traffic.Tick(Frame);
            Assert.Greater(Position(traffic, "v").x, start + 1, "driving along its road again");
        }

        [Test]
        public void TheTrafficReplyIsRead()
        {
            // Packed by the backend (struct "<HiihH"): two vehicles, the second at the edges of what the fields hold
            const string packed = "BwA5MAAAWv3//4QD6APxA3Av/P8ACT0A+Pj//w==";
            const string reply = "{\"session_id\":\"s1\",\"frames\":[{\"tick\":4,\"simulation_time_s\":0.2,\"vehicle_count\":2,\"vehicles\":\"" + packed + "\","
                + "\"shuttles\":[{\"vehicle_id\":\"shuttle-01\",\"x_m\":1.5,\"y_m\":-2.5,\"heading_deg\":45.0,\"speed_mps\":3.0}]}]}";
            var frames = TrainingApi.ParseTrafficFrames(reply, "s1");
            Assert.AreEqual(1, frames.Count);
            Assert.AreEqual(4, frames[0].Tick);
            Assert.AreEqual(0.2, frames[0].SimulationTimeS, 1e-9);
            CollectionAssert.AreEqual(new[] { 7, 12345, -678, 900, 1000, 1009, -250000, 4000000, -1800, 65535 }, frames[0].Vehicles);
            Assert.AreEqual(packed, TrafficFrame.PackVehicles(frames[0].Vehicles), "packed as the backend packs them");
            Assert.AreEqual("shuttle-01", frames[0].Shuttles[0].VehicleId);
            Assert.AreEqual(-2.5, frames[0].Shuttles[0].YM, 1e-9);
            Assert.Throws<Formula9.Contracts.ContractException>(() => TrainingApi.ParseTrafficFrames(reply, "s2"), "another session's");
            Assert.Throws<Formula9.Contracts.ContractException>(() => TrainingApi.ParseTrafficFrames(reply.Replace("\"vehicle_count\":2", "\"vehicle_count\":3"), "s1"), "a miscounted frame");
            Assert.Throws<Formula9.Contracts.ContractException>(() => TrainingApi.ParseTrafficFrames(reply.Replace(packed, "BwA5MAAA"), "s1"), "a vehicle cut short");
            Assert.Throws<Formula9.Contracts.ContractException>(() => TrainingApi.ParseTrafficFrames(
                "{\"session_id\":\"s1\",\"frames\":[{\"tick\":4,\"simulation_time_s\":0.2,\"vehicle_count\":1,\"vehicles\":[7,1,2,3,4]}]}", "s1"), "unpacked vehicles");
        }

        // Feeds frames to the traffic as a backend would send them, one each interval of real time, and
        // draws it every frame; returns the vehicle's drawn place each frame.
        private static List<Vector3> Play(CityTrafficRenderer traffic, IReadOnlyList<TrafficFrame> frames, double interval = 0.1, float frame = 1 / 60f, double extraS = 1)
        {
            var drawn = new List<Vector3>();
            var next = 0;
            for (var now = 0.0; now < frames.Count * interval + extraS; now += frame)
            {
                while (next < frames.Count && next * interval <= now) traffic.FollowBackend(new[] { frames[next++] }, now);
                traffic.Tick(frame);
                if (traffic.TryGetPose("v", out var position, out _)) drawn.Add(position);
            }
            return drawn;
        }

        [Test]
        public void DrawnBetweenFramesTheTrafficRoundsABendWithoutCorners()
        {
            // Round a bend of 20 m radius at 10 m/s: one metre a frame, the heading turning 2.9 degrees a frame
            const float radius = 20, step = 1f / radius;
            Vector2 At(int k) => new Vector2(radius * Mathf.Sin(k * step), radius - radius * Mathf.Cos(k * step));
            Vector2 Facing(int k) => new Vector2(Mathf.Cos(k * step), Mathf.Sin(k * step));
            for (var k = 1; k < 4; k++)
            {
                // Leaving a frame along its heading, and arriving at the next along that one's
                CityTrafficRenderer.Between(At(k), Facing(k), At(k + 1), Facing(k + 1), 0.001f, out var leaving);
                CityTrafficRenderer.Between(At(k - 1), Facing(k - 1), At(k), Facing(k), 0.999f, out var arriving);
                Assert.Less(Vector2.Angle(leaving - At(k), Facing(k)), 0.5f, "leaves along its heading");
                Assert.Less(Vector2.Angle(At(k) - arriving, Facing(k)), 0.5f, "so no corner where two frames meet");
                // Halfway, on the bend rather than cutting across it
                CityTrafficRenderer.Between(At(k), Facing(k), At(k + 1), Facing(k + 1), 0.5f, out var halfway);
                Assert.AreEqual(radius, Vector2.Distance(halfway, new Vector2(0, radius)), 0.0005f);
            }
            // Moved sideways, or turned about, it goes straight across
            CityTrafficRenderer.Between(Vector2.zero, Vector2.right, new Vector2(0, 3), Vector2.right, 0.5f, out var across);
            Assert.AreEqual(new Vector2(0, 1.5f), across);
        }

        [Test]
        public void AHeadingTurnsSmoothlyThroughTheFrames()
        {
            // A steady turn is drawn turning steadily, across north's 0/360
            Assert.AreEqual(0, Mathf.DeltaAngle(0, CityTrafficRenderer.HeadingBetween(350, 10, 330, 30, 0.5f)), 1e-3f);
            Assert.AreEqual(5, CityTrafficRenderer.HeadingBetween(0, 10, -10, 20, 0.5f), 1e-3f);
            // Without the frames before and after, it turns at a steady rate between the two
            Assert.AreEqual(2.5f, CityTrafficRenderer.HeadingBetween(0, 10, null, null, 0.25f), 1e-3f);
            // Coming out of a turn onto a straight, the rate of turn eases off across the frame instead of stopping at it
            float Rate(float b, float c, float? a, float? d, float s) =>
                (CityTrafficRenderer.HeadingBetween(b, c, a, d, s + 0.001f) - CityTrafficRenderer.HeadingBetween(b, c, a, d, s - 0.001f)) / 0.002f;
            Assert.AreEqual(Rate(10, 20, 0, 20, 0.999f), Rate(20, 20, 10, 20, 0.001f), 0.5f);
        }

        [Test]
        public void AVehicleTheBackendPutsInAnotherLaneIsSteeredAcrossNotJumped()
        {
            var traffic = Traffic("car", "r", 1, 10, Road("r", 1, 2, true, "primary", (0, 0), (2000, 0)));
            // At 10 m/s along y = 0, then the backend has it in a lane 3 m over, as onto another road at a junction
            var frames = Enumerable.Range(0, 40).Select(k => BackendFrame(200 + 2 * k, 100 + k, k < 15 ? 0 : 3)).ToList();
            var drawn = Play(traffic, frames);
            for (var i = 1; i < drawn.Count; i++)
                Assert.Less(Vector3.Distance(drawn[i], drawn[i - 1]), 0.6f, $"no jump at frame {i}");
            Assert.AreEqual(3, drawn[drawn.Count - 1].z, 0.02f, "in the new lane in the end");
            Assert.AreEqual(139, drawn[drawn.Count - 1].x, 0.01f, "where the newest frame is once they stop coming");
        }

        [Test]
        public void TrafficFramesThatComeAtTheWallClocksPaceAreDrawnAtAnEvenSpeed()
        {
            var traffic = Traffic("car", "r", 1, 10, Road("r", 1, 2, true, "primary", (0, 0), (2000, 0)));
            var frames = Enumerable.Range(0, 100).Select(k => BackendFrame(200 + 2 * k, 100 + k)).ToList();
            var drawn = Play(traffic, frames, extraS: 0);
            // Once settled, a frame of 1/60 s moves the car the 10 m/s's worth, give or take what the clock corrects
            var settled = drawn.Skip(drawn.Count / 3).ToList();
            for (var i = 1; i < settled.Count; i++)
                Assert.AreEqual(10f / 60, settled[i].x - settled[i - 1].x, 10f / 60 * 0.15f, $"frame {i}");
            Assert.AreEqual(0, traffic.Playback.NearStalls);
        }
    }
}
