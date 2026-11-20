using System.Collections.Generic;
using System.Linq;
using Formula9.Backend;
using Formula9.Contracts;
using Formula9.World;
using NUnit.Framework;
using UnityEngine;

namespace Formula9.Tests
{
    // The walk network in the walkways file (sidewalks plan, phase 2) and the crowd that walks it here
    public class PedestrianTests
    {
        // Two sidewalks along y = 4 and y = -4, joined by a signalled crossing at x = 20 and a zebra at x = 0,
        // and a link round the corner at their west end:
        //
        //   0 ------------- 1 ------------- 2        y = 4
        //   |link           | zebra         | signal (J1, phase 0)
        //   3 ------------- 4 ------------- 5        y = -4
        //  x=-20            x=0             x=20
        private const string Network = @"{
            ""area"": ""test"", ""version"": 2, ""source_hash"": ""abc"",
            ""constants"": {""surface_y"": 0.1, ""kerb_rise_m"": 0.15, ""kerb_stone_m"": 0.15, ""ramp_depth_m"": 1.5},
            ""render"": {""tiles"": []},
            ""nodes"": [
                {""id"":0,""xy_m"":[-20,4],""kind"":""joint""}, {""id"":1,""xy_m"":[0,4],""kind"":""kerb""},
                {""id"":2,""xy_m"":[20,4],""kind"":""kerb""}, {""id"":3,""xy_m"":[-20,-4],""kind"":""joint""},
                {""id"":4,""xy_m"":[0,-4],""kind"":""kerb""}, {""id"":5,""xy_m"":[20,-4],""kind"":""kerb""}],
            ""walkways"": [
                {""id"":0,""kind"":""sidewalk"",""from"":0,""to"":1,""points_m"":[[-20,4],[0,4]],""length_m"":20,""width_m"":1.8,""lanes"":2,""road_id"":""way/1/1"",""side"":""left""},
                {""id"":1,""kind"":""sidewalk"",""from"":1,""to"":2,""points_m"":[[0,4],[20,4]],""length_m"":20,""width_m"":1.8,""lanes"":2,""road_id"":""way/1/1"",""side"":""left""},
                {""id"":2,""kind"":""sidewalk"",""from"":3,""to"":4,""points_m"":[[-20,-4],[0,-4]],""length_m"":20,""width_m"":1.8,""lanes"":2,""road_id"":""way/1/1"",""side"":""right""},
                {""id"":3,""kind"":""sidewalk"",""from"":4,""to"":5,""points_m"":[[0,-4],[20,-4]],""length_m"":20,""width_m"":1.8,""lanes"":2,""road_id"":""way/1/1"",""side"":""right""},
                {""id"":4,""kind"":""link"",""from"":0,""to"":3,""points_m"":[[-20,4],[-24,0],[-20,-4]],""length_m"":11.31,""width_m"":1.8,""lanes"":2},
                {""id"":5,""kind"":""crossing"",""from"":1,""to"":4,""points_m"":[[0,4],[0,2.2],[0,-2.2],[0,-4]],""length_m"":8,""width_m"":3.0,""lanes"":4,""crossing_id"":""crossing-001"",""control"":""zebra""},
                {""id"":6,""kind"":""crossing"",""from"":2,""to"":5,""points_m"":[[20,4],[20,2.2],[20,-2.2],[20,-4]],""length_m"":8,""width_m"":3.0,""lanes"":4,""crossing_id"":""crossing-002"",""control"":""signal"",""junction_id"":""J1"",""phase"":0}],
            ""seeds"": [
                {""id"":""ped-0001"",""walkway"":0,""distance_m"":5,""speed_mps"":1.3,""height_m"":1.7,""model"":2},
                {""id"":""ped-0002"",""walkway"":1,""distance_m"":12,""speed_mps"":1.1,""height_m"":1.6,""model"":0},
                {""id"":""ped-0003"",""walkway"":2,""distance_m"":19,""speed_mps"":1.5,""height_m"":1.8,""model"":5},
                {""id"":""ped-0004"",""walkway"":3,""distance_m"":2,""speed_mps"":1.2,""height_m"":1.55,""model"":1},
                {""id"":""ped-0005"",""walkway"":4,""distance_m"":6,""speed_mps"":1.4,""height_m"":1.75,""model"":7}],
            ""report"": {""walkways"": 7, ""crossing_walkways"": 2, ""unattached_crossings"": 1, ""unmatched_signal_crossings"": 0, ""components"": 1, ""main_component_share"": 1.0}
        }";

        private static CitySignalData Light(int greenS = 30) => new CitySignalData
        {
            Timing = new CitySignalTiming { GreenS = greenS, YellowS = 3, AllRedS = 2, Phases = 2 },
            Junctions = new List<CitySignalJunction> { new CitySignalJunction { Id = "J1", OffsetS = 0 } },
        };

        private static float DistanceToWalkway(CityWalkway way, Vector2 at)
        {
            var best = float.MaxValue;
            for (var i = 0; i + 3 < way.PointsM.Length; i += 2)
            {
                var a = new Vector2(way.PointsM[i], way.PointsM[i + 1]);
                var b = new Vector2(way.PointsM[i + 2], way.PointsM[i + 3]);
                var run = b - a;
                var t = run.sqrMagnitude <= 0 ? 0 : Mathf.Clamp01(Vector2.Dot(at - a, run) / run.sqrMagnitude);
                best = Mathf.Min(best, Vector2.Distance(at, a + run * t));
            }
            return best;
        }

        // Within a walkway's width of some walkway
        private static bool OnAWalkway(CityWalkwayData data, Vector3 position)
        {
            var at = new Vector2(position.x, position.z);
            return data.Walkways.Any(way => DistanceToWalkway(way, at) <= way.WidthM / 2 + 0.05f);
        }

        [Test]
        public void TheWalkNetworkIsReadFromTheWalkwaysFile()
        {
            var data = CityWalkwayData.Parse(Network);
            Assert.AreEqual(6, data.Nodes.Count);
            Assert.AreEqual(new Vector2(20, -4), new Vector2(data.Nodes[5].X, data.Nodes[5].Y));
            Assert.AreEqual("kerb", data.Nodes[1].Kind);
            Assert.AreEqual(7, data.Walkways.Count);
            var link = data.Walkways[4];
            Assert.AreEqual(CityWalkwayKind.Link, link.Kind);
            Assert.AreEqual(new float[] { -20, 4, -24, 0, -20, -4 }, link.PointsM, "points are flattened");
            Assert.AreEqual(CityCrossingControl.None, link.Control);
            var signal = data.Walkways[6];
            Assert.AreEqual(CityWalkwayKind.Crossing, signal.Kind);
            Assert.AreEqual(CityCrossingControl.Signal, signal.Control);
            Assert.AreEqual("J1", signal.JunctionId);
            Assert.AreEqual(4, signal.Lanes);
            Assert.AreEqual(CityCrossingControl.Zebra, data.Walkways[5].Control);
            Assert.AreEqual("left", data.Walkways[0].Side);
            Assert.AreEqual(5, data.Seeds.Count);
            Assert.AreEqual(1.55f, data.Seeds[3].HeightM, 1e-6f);
            Assert.AreEqual(2, data.Report.CrossingWalkways);
            Assert.AreEqual(1, data.Report.UnattachedCrossings);
        }

        [Test]
        public void AFileFromBeforeTheWalkNetworkHasNone()
        {
            var data = CityWalkwayData.Parse(Network.Substring(0, Network.IndexOf("\"nodes\"")) + "\"report\": {}}");
            Assert.IsEmpty(data.Nodes);
            Assert.IsEmpty(data.Walkways);
            Assert.IsEmpty(data.Seeds);
        }

        [TestCase("\"from\":4,\"to\":5", "\"from\":4,\"to\":9")]
        [TestCase("\"from\":0,\"to\":1", "\"from\":1,\"to\":1")]
        [TestCase("\"control\":\"zebra\"", "\"control\":\"maybe\"")]
        [TestCase("\"control\":\"zebra\"", "\"note\":\"none\"")]
        [TestCase(",\"junction_id\":\"J1\"", "")]
        [TestCase("\"walkway\":4,\"distance_m\":6", "\"walkway\":4,\"distance_m\":60")]
        [TestCase("\"walkway\":4,", "\"walkway\":40,")]
        [TestCase("\"kind\":\"link\"", "\"kind\":\"path\"")]
        public void ABrokenWalkNetworkIsRefused(string from, string to)
        {
            Assert.IsTrue(Network.Contains(from), from);
            Assert.Throws<ContractException>(() => CityWalkwayData.Parse(Network.Replace(from, to)));
        }

        [Test]
        public void TheLocalCrowdStartsWhereTheSeedsSayAndStaysOnTheWalkways()
        {
            var data = CityWalkwayData.Parse(Network);
            var sim = new PedestrianLocalSim();
            sim.SetSignalClock(() => 40);
            sim.Rebuild(data, Light());
            Assert.AreEqual(6, sim.NodeCount);
            Assert.AreEqual(7, sim.WalkwayCount);
            Assert.AreEqual(data.Report.CrossingWalkways, sim.CrossingCount, "the crossings the importer made");
            Assert.AreEqual(0, sim.CrossingsWithoutLight);
            Assert.AreEqual(data.Seeds.Count, sim.AgentCount);
            for (var i = 0; i < data.Seeds.Count; i++)
            {
                Assert.AreEqual(data.Seeds[i].HeightM, sim.Poses[i].ScaleM, 1e-6f);
                Assert.AreEqual(data.Seeds[i].Model, sim.Poses[i].Model);
                Assert.IsTrue(OnAWalkway(data, sim.Poses[i].Position), $"walker {i} starts on its walkway");
            }
            var seen = new HashSet<int>();
            for (var step = 0; step < 3000; step++)
            {
                sim.Tick(0.1f);
                for (var i = 0; i < sim.Poses.Count; i++)
                {
                    var pose = sim.Poses[i];
                    Assert.IsTrue(OnAWalkway(data, pose.Position), $"walker {i} at {pose.Position} after {step} steps");
                    Assert.AreEqual(RoadMesh.SurfaceY, pose.Position.y, 1e-6f, "the road's height with no sidewalks given");
                    if (pose.Position.x < -20.5f) seen.Add(i);
                }
            }
            Assert.IsNotEmpty(seen, "someone took the link round the end");
        }

        [Test]
        public void KeepingLeftPeopleWalkingOppositeWaysPassSideBySide()
        {
            var data = CityWalkwayData.Parse(Network);
            var sim = new PedestrianLocalSim();
            sim.Rebuild(data, Light());
            sim.Tick(0.1f);
            for (var i = 0; i < sim.Poses.Count; i++)
            {
                var pose = sim.Poses[i];
                if (Mathf.Abs(Mathf.Abs(pose.Position.z) - 4) > 0.5f || !pose.Moving) continue;
                var heading = pose.Rotation * Vector3.forward;
                var left = new Vector2(-heading.z, heading.x);
                var off = new Vector2(pose.Position.x, pose.Position.z) - new Vector2(pose.Position.x, Mathf.Sign(pose.Position.z) * 4);
                Assert.Greater(Vector2.Dot(off, left), 0.2f, $"walker {i} keeps to the left of the walkway's middle");
            }
        }

        [Test]
        public void ASignalledCrossingIsOnlyStartedOnRedAndOneWithoutItsLightIsLeftOut()
        {
            var data = CityWalkwayData.Parse(Network);
            var time = 5.0;
            var sim = new PedestrianLocalSim();
            sim.SetSignalClock(() => time);
            sim.Rebuild(data, Light());
            // The traffic's green for the first 30 s of each 70: nobody steps onto the signalled crossing
            for (var step = 0; step < 200; step++)
            {
                time = 5 + step * 0.1;
                sim.Tick(0.1f);
                foreach (var pose in sim.Poses)
                    Assert.IsFalse(Mathf.Abs(pose.Position.x - 20) < 1.6f && Mathf.Abs(pose.Position.z) < 2.2f,
                        $"on the signalled crossing at {pose.Position} while the traffic had green");
            }

            var dark = new PedestrianLocalSim();
            dark.Rebuild(data, null);
            Assert.AreEqual(1, dark.CrossingsWithoutLight, "no lights, so no telling when to cross");
            Assert.AreEqual(1, dark.CrossingCount);
            Assert.AreEqual(6, dark.WalkwayCount);
        }

        [Test]
        public void PeopleStandOnTheSidewalksTopOnceItIsIn()
        {
            var data = CityWalkwayData.Parse(Network);
            var sim = new PedestrianLocalSim();
            sim.Rebuild(data, Light());
            sim.SetGroundHeight(_ => SidewalkMesh.TopY);
            Assert.IsTrue(sim.Poses.All(pose => Mathf.Abs(pose.Position.y - SidewalkMesh.TopY) < 1e-6f));
            sim.Tick(0.1f);
            Assert.IsTrue(sim.Poses.All(pose => Mathf.Abs(pose.Position.y - SidewalkMesh.TopY) < 1e-6f));
        }

        // The bundled Toa Payoh file: the crowd walks the network the importer made, crossing for crossing
        [Test]
        public void TheBundledToaPayohCrowdWalksTheImportersNetwork()
        {
            var walkways = Resources.Load<TextAsset>("Formula9/RoadData/toa-payoh-walkways");
            var lights = Resources.Load<TextAsset>("Formula9/RoadData/toa-payoh-signals");
            Assert.IsNotNull(walkways);
            Assert.IsNotNull(lights);
            var data = CityWalkwayData.Parse(walkways.text);
            Assert.Greater(data.Walkways.Count, 1000);
            Assert.AreEqual(data.Report.Walkways, data.Walkways.Count);
            Assert.AreEqual(600, data.Seeds.Count);

            var sim = new PedestrianLocalSim();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            sim.Rebuild(data, CityClient.ParseSignals(lights.text));
            clock.Stop();
            Assert.AreEqual(data.Report.CrossingWalkways, sim.CrossingCount, "every crossing the importer made, and no other");
            Assert.AreEqual(0, sim.CrossingsWithoutLight, "every signalled crossing's junction is among the lights");
            Assert.AreEqual(data.Walkways.Count, sim.WalkwayCount);
            Assert.AreEqual(data.Seeds.Count, sim.AgentCount);
            Assert.Less(clock.ElapsedMilliseconds, 500, "reading the network, not working one out");

            for (var step = 0; step < 100; step++) sim.Tick(0.1f);
            for (var i = 0; i < sim.Poses.Count; i += 7)
                Assert.IsTrue(OnAWalkway(data, sim.Poses[i].Position), $"walker {i} at {sim.Poses[i].Position}");
        }
    }
}
