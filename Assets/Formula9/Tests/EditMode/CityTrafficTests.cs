using System.Collections.Generic;
using System.Linq;
using Formula9.Backend;
using Formula9.World;
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
