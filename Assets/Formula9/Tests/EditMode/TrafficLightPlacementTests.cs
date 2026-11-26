using System.Collections.Generic;
using System.IO;
using System.Linq;
using Formula9.Backend;
using Formula9.World;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using static System.Net.Mime.MediaTypeNames;

namespace Formula9.Tests
{
    public class TrafficLightPlacementTests
    {
        private static CitySignalTrafficLight Head(double x, double y, double trafficHeadingDeg, string junction = "j") => new CitySignalTrafficLight
        {
            Junction = junction,
            XM = x,
            YM = y,
            TrafficHeadingDeg = trafficHeadingDeg,
            FacingHeadingDeg = (trafficHeadingDeg + 180) % 360,
        };

        // Running north along x = 0, with one lane each way: 3.8 m of asphalt each side of the centreline
        private static List<CityRoad> NorthRoad() => new List<CityRoad>
        {
            new CityRoad { Id = "r", Class = "primary", Lanes = 1, Oneway = false, Points = new List<(double X, double Y)> { (0, -100), (0, 100) } },
        };

        [Test]
        public void HeadsOfOnePoleBecomeOneLight()
        {
            var kept = TrafficLightPlacement.OnePerPole(new[] { Head(0, 0, 90), Head(0, 0, 90), Head(2, 2, 100) });
            Assert.AreEqual(1, kept.Count);
        }

        [Test]
        public void DirectionsEitherSideOfNorthAreStillTheSameDirection()
        {
            var kept = TrafficLightPlacement.OnePerPole(new[] { Head(0, 0, 179), Head(0, 0, 185) });
            Assert.AreEqual(1, kept.Count);
            kept = TrafficLightPlacement.OnePerPole(new[] { Head(0, 0, 359), Head(0, 0, 2) });
            Assert.AreEqual(1, kept.Count);
        }

        [Test]
        public void HeadsAtOtherJunctionsOrFacingOtherWaysAreKept()
        {
            Assert.AreEqual(2, TrafficLightPlacement.OnePerPole(new[] { Head(0, 0, 90), Head(0, 0, 90, "other") }).Count);
            Assert.AreEqual(2, TrafficLightPlacement.OnePerPole(new[] { Head(0, 0, 90), Head(0, 0, 270) }).Count);
        }

        [Test]
        public void APoleOnTheKerbStaysWhereLtaPutItAndReachesOverTheRoad()
        {
            // Traffic heading north keeps to the west side, with the road on the driver's right
            var pose = TrafficLightPlacement.Place(Head(-4.5, 0, 90), NorthRoad());
            Assert.AreEqual(-4.5f, pose.Position.x, 1e-3f);
            Assert.IsFalse(pose.Mirrored);
            var arm = pose.Rotation * Vector3.right;
            Assert.AreEqual(1, arm.x, 1e-3f);
        }

        [Test]
        public void APoleAcrossTheRoadFromItsTrafficReachesBackOverTheRoad()
        {
            Assert.IsTrue(TrafficLightPlacement.Place(Head(4.5, 0, 90), NorthRoad()).Mirrored);
        }

        [Test]
        public void APoleOnTheRoadMovesToTheNearestSide()
        {
            var pose = TrafficLightPlacement.Place(Head(-2, 0, 90), NorthRoad());
            Assert.Less(pose.Position.x, -3.8f);
            Assert.Less(Vector3.Distance(pose.Position, new Vector3(-2, 0, 0)), 4);
        }

        [Test]
        public void WithoutRoadsAPoleStaysPut()
        {
            var pose = TrafficLightPlacement.Place(Head(-2, 5, 90), new List<CityRoad>());
            Assert.AreEqual(new Vector3(-2, 0, 5), pose.Position);
        }

        [Test]
        public void ThePrefabsHeadSitsAtTheEndOfItsArmAndFacesOncomingTraffic()
        {
            var prefab = Resources.Load<GameObject>("Formula9/TrafficLights");
            Assert.IsNotNull(prefab);
            var head = prefab.transform.Find("tl_post_01/TrafficLight_Fwd");
            Assert.IsNotNull(head);
            Assert.AreEqual(TrafficLightPlacement.ArmM, head.localPosition.x, 1e-3f);
            // The lights' halos are in front of their lenses, and the prefab's +Z is the way traffic travels
            var halo = head.Find("green_halo");
            Assert.Less(0, prefab.transform.InverseTransformPoint(halo.position).z);
        }

        [Test]
        public void TheFixtureGivesEachHeadItsJunctionsOffset()
        {
            var fixture = Path.Combine(UnityEngine.Application.dataPath, "..", "backend", "fixtures", "traffic", "toa-payoh-signals.json");
            var signals = CityClient.ParseSignalTrafficLights(File.ReadAllText(fixture));
            var junctions = CityClient.ParseSignals(File.ReadAllText(fixture)).Junctions.ToDictionary(j => j.Id);
            Assert.IsTrue(signals.All(s => (int)s.OffsetS == junctions[s.Junction].OffsetS));
            Assert.Greater(signals.Select(s => s.OffsetS).Distinct().Count(), 1);
        }

        [Test]
        public void TheFixturesPolesAreOffTheRoadAndMostArmsOverIt()
        {
            var traffic = Path.Combine(UnityEngine.Application.dataPath, "..", "backend", "fixtures", "traffic");
            var all = CityClient.ParseSignalTrafficLights(File.ReadAllText(Path.Combine(traffic, "toa-payoh-signals.json")));
            var signals = TrafficLightPlacement.OnePerPole(all);
            var roads = JObject.Parse(File.ReadAllText(Path.Combine(traffic, "toa-payoh.json")))["roads"].Select(r => new CityRoad
            {
                Id = (string)r["id"],
                Class = (string)r["class"],
                Oneway = (bool)r["oneway"],
                Lanes = (int)r["lanes"],
                Points = r["points_m"].Select(p => ((double)p[0], (double)p[1])).ToList(),
            }).ToList();

            bool OnRoad(Vector3 point) => roads.Any(road =>
            {
                for (var i = 0; i + 1 < road.Points.Count; i++)
                {
                    var a = new Vector2((float)road.Points[i].X, (float)road.Points[i].Y);
                    var b = new Vector2((float)road.Points[i + 1].X, (float)road.Points[i + 1].Y);
                    var share = Mathf.Clamp01(Vector2.Dot(new Vector2(point.x, point.z) - a, b - a) / Mathf.Max((b - a).sqrMagnitude, 1e-6f));
                    if (Vector2.Distance(new Vector2(point.x, point.z), a + (b - a) * share) < RoadMesh.HalfWidth(road)) return true;
                }
                return false;
            });

            int polesOnRoad = 0, armsOverRoad = 0;
            foreach (var signal in signals)
            {
                var pose = TrafficLightPlacement.Place(signal, roads);
                if (OnRoad(pose.Position)) polesOnRoad++;
                if (OnRoad(pose.Position + pose.Rotation * (Vector3.right * (pose.Mirrored ? -1 : 1) * TrafficLightPlacement.ArmM))) armsOverRoad++;
            }
            Debug.LogWarning($"Traffic lights: {all.Count} heads, {signals.Count} poles, {polesOnRoad} on the road, {armsOverRoad} with an arm over it");
            Assert.AreEqual(0, polesOnRoad);
            Assert.Greater(armsOverRoad, signals.Count / 2);
        }
    }
}
