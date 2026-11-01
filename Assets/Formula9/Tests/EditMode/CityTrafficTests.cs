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

        // The real roads, vehicles and lights, as the backend serves them, for four minutes of traffic.
        [Test]
        public void TheRealCityKeepsMovingWithNoOverlapsOrRedLightRuns()
        {
            var folder = Path.Combine(Application.dataPath, "..", "backend", "fixtures", "traffic");
            var root = JObject.Parse(File.ReadAllText(Path.Combine(folder, "toa-payoh.json")));
            var roads = root["roads"].Select(r => new CityRoad
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
    }
}
